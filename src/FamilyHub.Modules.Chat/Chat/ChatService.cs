using System.Globalization;
using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Chat;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Chat.Realtime;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Modules.Chat.Chat;

/// <summary>
/// Общий чат семьи (ADR-0019) — семейный ресурс: всё доступно только активному члену семьи
/// (<see cref="IFamilyAccessService.HasRoleAsync"/> на каждой операции, инвариант 1 — сообщение никогда не
/// грузится по Id без FamilyId). Править — только автор; удалять — автор или админ семьи (модерация).
/// После каждого изменения — событие хаба (только id) и, для нового сообщения, задача уведомления.
/// </summary>
public class ChatService(
    AppDbContext db,
    IFamilyAccessService access,
    ChatAttachmentStore attachments,
    IEnumerable<IChatLinkResolver> linkResolvers,
    IChatEvents events,
    IBackgroundJobClient backgroundJobs,
    IOptions<ChatOptions> options,
    ILogger<ChatService> logger)
{
    /// <summary>Фиксированный набор реакций — произвольные эмодзи не принимаются.</summary>
    public static readonly IReadOnlyList<string> Reactions = ["👍", "❤️", "😂", "😮", "😢", "🙏"];

    public const int ReplySnippetLength = 140;

    private ChatOptions Limits => options.Value;

    public ChatConfigDto GetConfig() => new(
        Limits.MaxTextLength, Limits.MaxFilesPerMessage, Limits.MaxFileSizeBytes, Limits.MaxLinksPerMessage,
        Reactions, DocumentContentTypes.All);

    // ─── Чтение ────────────────────────────────────────────────────────────────────────────────────

    public async Task<(ChatResult Result, ChatPageDto? Page)> GetPageAsync(
        Guid familyId, Guid userId, string? before, int? limit, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct))
        {
            logger.LogWarning("Чат: лента семьи {FamilyId} отклонена — {UserId} не активный участник", familyId, userId);
            return (ChatResult.Forbidden, null);
        }

        var cursor = ParseCursor(before);
        if (before is not null && cursor is null) return (ChatResult.Invalid, null);

        var take = Math.Clamp(limit ?? Limits.DefaultPageSize, 1, Limits.MaxPageSize);
        var query = MessagesWithDetails().Where(m => m.FamilyId == familyId);
        if (cursor is { } c)
            query = query.Where(m => m.CreatedAt < c.CreatedAt || (m.CreatedAt == c.CreatedAt && m.Id.CompareTo(c.Id) < 0));

        var page = await query
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(take + 1)
            .ToListAsync(ct);

        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);

        var items = await ToDtosAsync(page, userId, ct);
        var nextCursor = hasMore ? FormatCursor(page[^1]) : null;
        return (ChatResult.Success, new ChatPageDto(items, nextCursor));
    }

    public async Task<(ChatResult Result, ChatMessageDto? Item)> GetMessageAsync(
        Guid familyId, Guid messageId, Guid userId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct))
            return (ChatResult.Forbidden, null);

        var message = await MessagesWithDetails().FirstOrDefaultAsync(m => m.Id == messageId && m.FamilyId == familyId, ct);
        if (message is null) return (ChatResult.NotFound, null);

        return (ChatResult.Success, (await ToDtosAsync([message], userId, ct))[0]);
    }

    /// <summary>Непрочитанное по всем активным семьям пользователя: чужие неудалённые сообщения позже его
    /// отметки прочтения и позже его вступления в семью (новичку не насчитываем всю историю).</summary>
    public async Task<List<ChatUnreadDto>> GetUnreadAsync(Guid userId, CancellationToken ct = default)
    {
        var familyIds = await access.GetActiveFamilyIdsAsync(userId, ct);
        if (familyIds.Count == 0) return [];

        var counts = await db.ChatMessages.AsNoTracking()
            .Where(m => familyIds.Contains(m.FamilyId) && m.AuthorUserId != userId && m.DeletedAt == null)
            .Where(m => db.FamilyMembers.Any(fm => fm.FamilyId == m.FamilyId && fm.UserId == userId && fm.JoinedAt < m.CreatedAt))
            .Where(m => !db.ChatReadStates.Any(s => s.FamilyId == m.FamilyId && s.UserId == userId && s.LastReadAt >= m.CreatedAt))
            .GroupBy(m => m.FamilyId)
            .Select(g => new { FamilyId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byFamily = counts.ToDictionary(c => c.FamilyId, c => c.Count);
        return familyIds.Select(f => new ChatUnreadDto(f, byFamily.GetValueOrDefault(f))).ToList();
    }

    // ─── Запись ────────────────────────────────────────────────────────────────────────────────────

    public async Task<(ChatResult Result, ChatMessageDto? Item, string? Error)> SendAsync(
        Guid familyId, Guid userId, SendChatMessageInput input, CancellationToken ct = default)
    {
        var text = (input.Text ?? string.Empty).Trim();
        var links = input.Links.Distinct().ToList();

        var error = ValidateContent(text, links.Count, input.Files);
        if (error is not null) return (ChatResult.Invalid, null, error);

        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct))
        {
            logger.LogWarning("Чат: отправка в семью {FamilyId} отклонена — {UserId} не активный участник", familyId, userId);
            return (ChatResult.Forbidden, null, null);
        }

        if (input.ReplyToMessageId is { } replyId
            && !await db.ChatMessages.AnyAsync(m => m.Id == replyId && m.FamilyId == familyId, ct))
            return (ChatResult.Invalid, null, "Сообщение, на которое вы отвечаете, не найдено.");

        // Ссылаться можно только на то, что видит сам автор, — иначе это была бы проверка «существует ли
        // запись с таким id» чужими руками.
        foreach (var link in links)
        {
            var preview = await ResolveLinkAsync(link.Kind, link.EntityId, userId, ct);
            if (!preview.Accessible) return (ChatResult.Invalid, null, "Ссылка ведёт на недоступный объект.");
        }

        var now = DateTime.UtcNow;
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId,
            AuthorUserId = userId,
            Text = text,
            ReplyToMessageId = input.ReplyToMessageId,
            CreatedAt = now,
            Links = links.Select(l => new ChatMessageLink { Id = Guid.NewGuid(), Kind = l.Kind, EntityId = l.EntityId }).ToList(),
        };

        // Блобы — до коммита строки (строки FileAttachment ссылаются на уже лежащие объекты); если коммит
        // не удался, подчищаем загруженное.
        var stored = new List<FileAttachment>();
        try
        {
            foreach (var file in input.Files)
                stored.Add(await attachments.StoreAsync(message.Id, file, ct));

            db.ChatMessages.Add(message);
            db.FileAttachments.AddRange(stored);
            await UpsertReadStateAsync(familyId, userId, now, ct);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await attachments.DeleteBlobsAsync(stored.Select(a => a.StorageKey), CancellationToken.None);
            throw;
        }

        logger.LogInformation(
            "Чат: сообщение {MessageId} в семье {FamilyId} от {UserId} (вложений {Files}, ссылок {Links})",
            message.Id, familyId, userId, stored.Count, links.Count);

        await events.MessageChangedAsync(familyId, message.Id, ChatChange.Created, ct);
        EnqueueNotification(message.Id);

        return (ChatResult.Success, (await GetMessageAsync(familyId, message.Id, userId, ct)).Item, null);
    }

    public async Task<(ChatResult Result, string? Error)> EditAsync(
        Guid familyId, Guid messageId, Guid userId, string? newText, CancellationToken ct = default)
    {
        var text = (newText ?? string.Empty).Trim();
        if (text.Length > Limits.MaxTextLength)
            return (ChatResult.Invalid, $"Сообщение длиннее {Limits.MaxTextLength} символов.");

        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct)) return (ChatResult.Forbidden, null);

        var message = await db.ChatMessages.Include(m => m.Links)
            .FirstOrDefaultAsync(m => m.Id == messageId && m.FamilyId == familyId && m.DeletedAt == null, ct);
        if (message is null) return (ChatResult.NotFound, null);
        if (message.AuthorUserId != userId) return (ChatResult.Forbidden, null);

        if (text.Length == 0 && message.Links.Count == 0
            && !await db.FileAttachments.AnyAsync(a => a.OwnerType == FileOwnerType.ChatMessage && a.OwnerId == messageId, ct))
            return (ChatResult.Invalid, "Сообщение не может стать пустым — удалите его.");

        if (message.Text == text) return (ChatResult.Success, null);

        message.Text = text;
        message.EditedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Чат: сообщение {MessageId} изменено автором {UserId}", messageId, userId);
        await events.MessageChangedAsync(familyId, messageId, ChatChange.Edited, ct);
        return (ChatResult.Success, null);
    }

    /// <summary>Мягкое удаление (ADR-0019 п. 8): содержимое стирается, строка остаётся под цитаты ответов.</summary>
    public async Task<ChatResult> DeleteAsync(Guid familyId, Guid messageId, Guid userId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct)) return ChatResult.Forbidden;

        var message = await db.ChatMessages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.FamilyId == familyId && m.DeletedAt == null, ct);
        if (message is null) return ChatResult.NotFound;

        var isModerator = message.AuthorUserId != userId;
        if (isModerator && !await access.HasRoleAsync(userId, familyId, FamilyRole.Admin, ct))
            return ChatResult.Forbidden;

        var files = await db.FileAttachments
            .Where(a => a.OwnerType == FileOwnerType.ChatMessage && a.OwnerId == messageId)
            .ToListAsync(ct);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            message.Text = string.Empty;
            message.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await db.ChatMessageReactions.Where(r => r.MessageId == messageId).ExecuteDeleteAsync(ct);
            await db.ChatMessageLinks.Where(l => l.MessageId == messageId).ExecuteDeleteAsync(ct);
            await db.FileAttachments
                .Where(a => a.OwnerType == FileOwnerType.ChatMessage && a.OwnerId == messageId)
                .ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }

        await attachments.DeleteBlobsAsync(files.Select(f => f.StorageKey), ct);

        logger.LogInformation(
            "Чат: сообщение {MessageId} семьи {FamilyId} удалено {Who} {UserId}",
            messageId, familyId, isModerator ? "админом" : "автором", userId);
        await events.MessageChangedAsync(familyId, messageId, ChatChange.Deleted, ct);
        return ChatResult.Success;
    }

    public async Task<ChatResult> SetReactionAsync(
        Guid familyId, Guid messageId, Guid userId, string emoji, bool on, CancellationToken ct = default)
    {
        if (!Reactions.Contains(emoji)) return ChatResult.Invalid;
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct)) return ChatResult.Forbidden;

        if (!await db.ChatMessages.AnyAsync(m => m.Id == messageId && m.FamilyId == familyId && m.DeletedAt == null, ct))
            return ChatResult.NotFound;

        var existing = await db.ChatMessageReactions
            .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId && r.Emoji == emoji, ct);
        if (on == (existing is not null)) return ChatResult.Success; // уже в нужном состоянии — идемпотентно

        if (on)
        {
            var reaction = new ChatMessageReaction { MessageId = messageId, UserId = userId, Emoji = emoji, CreatedAt = DateTime.UtcNow };
            db.ChatMessageReactions.Add(reaction);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Двойной клик: параллельный запрос уже вставил ту же реакцию (PK на тройку) — это и есть успех.
                db.Entry(reaction).State = EntityState.Detached;
                return ChatResult.Success;
            }
        }
        else
        {
            db.ChatMessageReactions.Remove(existing!);
            await db.SaveChangesAsync(ct);
        }

        await events.MessageChangedAsync(familyId, messageId, ChatChange.Reactions, ct);
        return ChatResult.Success;
    }

    /// <summary>Отметка «прочитано до» — только вперёд и не дальше «сейчас».</summary>
    public async Task<ChatResult> MarkReadAsync(Guid familyId, Guid userId, DateTime? upTo, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct)) return ChatResult.Forbidden;

        var now = DateTime.UtcNow;
        var at = upTo is { } u && u.ToUniversalTime() < now ? u.ToUniversalTime() : now;
        await UpsertReadStateAsync(familyId, userId, at, ct);
        await db.SaveChangesAsync(ct);
        return ChatResult.Success;
    }

    // ─── Внутреннее ────────────────────────────────────────────────────────────────────────────────

    private string? ValidateContent(string text, int linkCount, IReadOnlyList<ChatUpload> files)
    {
        if (text.Length == 0 && linkCount == 0 && files.Count == 0) return "Пустое сообщение.";
        if (text.Length > Limits.MaxTextLength) return $"Сообщение длиннее {Limits.MaxTextLength} символов.";
        if (linkCount > Limits.MaxLinksPerMessage) return $"Не больше {Limits.MaxLinksPerMessage} ссылок в сообщении.";
        if (files.Count > Limits.MaxFilesPerMessage) return $"Не больше {Limits.MaxFilesPerMessage} файлов в сообщении.";
        foreach (var file in files)
        {
            if (file.SizeBytes <= 0) return "Пустой файл.";
            if (file.SizeBytes > Limits.MaxFileSizeBytes)
                return $"Файл больше {Limits.MaxFileSizeBytes / (1024 * 1024)} МБ.";
            if (!DocumentContentTypes.All.Contains(file.ContentType)) return "Такой тип файла нельзя отправить.";
        }
        return null;
    }

    private async Task UpsertReadStateAsync(Guid familyId, Guid userId, DateTime at, CancellationToken ct)
    {
        var state = await db.ChatReadStates.FirstOrDefaultAsync(s => s.FamilyId == familyId && s.UserId == userId, ct);
        if (state is null)
            db.ChatReadStates.Add(new ChatReadState { FamilyId = familyId, UserId = userId, LastReadAt = at });
        else if (at > state.LastReadAt)
            state.LastReadAt = at;
    }

    private void EnqueueNotification(Guid messageId)
    {
        // Best-effort после коммита: сбой планировщика не должен превращать отправленное сообщение в ошибку.
        try
        {
            backgroundJobs.Enqueue<ChatMessageNotifyJob>(j => j.RunAsync(messageId, CancellationToken.None));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Чат: не удалось поставить уведомление о сообщении {MessageId} в очередь", messageId);
        }
    }

    private IQueryable<ChatMessage> MessagesWithDetails() =>
        db.ChatMessages.AsNoTracking()
            .Include(m => m.Author)
            .Include(m => m.Reactions)
            .Include(m => m.Links)
            .Include(m => m.ReplyTo).ThenInclude(r => r!.Author)
            .AsSplitQuery();

    private async Task<List<ChatMessageDto>> ToDtosAsync(List<ChatMessage> messages, Guid viewerUserId, CancellationToken ct)
    {
        if (messages.Count == 0) return [];

        var familyId = messages[0].FamilyId;
        var isAdmin = await access.HasRoleAsync(viewerUserId, familyId, FamilyRole.Admin, ct);

        var ownerIds = messages.Select(m => m.Id)
            .Concat(messages.Where(m => m.ReplyToMessageId != null).Select(m => m.ReplyToMessageId!.Value))
            .Distinct()
            .ToList();
        var files = await db.FileAttachments.AsNoTracking()
            .Where(a => a.OwnerType == FileOwnerType.ChatMessage && ownerIds.Contains(a.OwnerId))
            .OrderBy(a => a.UploadedAt)
            .ToListAsync(ct);
        var filesByMessage = files.ToLookup(a => a.OwnerId);

        var result = new List<ChatMessageDto>(messages.Count);
        foreach (var m in messages)
        {
            var deleted = m.DeletedAt is not null;
            var links = new List<ChatLinkDto>();
            if (!deleted)
            {
                foreach (var link in m.Links)
                {
                    var p = await ResolveLinkAsync(link.Kind, link.EntityId, viewerUserId, ct);
                    links.Add(new ChatLinkDto(p.Kind, p.EntityId, p.Accessible, p.Title, p.Subtitle, p.RouteKind));
                }
            }

            var reactions = m.Reactions
                .GroupBy(r => r.Emoji)
                .OrderBy(g => g.Min(r => r.CreatedAt))
                .Select(g => new ChatReactionDto(g.Key, g.Count(), g.Any(r => r.UserId == viewerUserId), g.Select(r => r.UserId).ToList()))
                .ToList();

            ChatReplyPreviewDto? reply = null;
            if (m.ReplyTo is { } r)
            {
                var replyDeleted = r.DeletedAt is not null;
                reply = new ChatReplyPreviewDto(
                    r.Id, ToAuthor(r.Author),
                    replyDeleted ? string.Empty : Snippet(r.Text),
                    replyDeleted,
                    replyDeleted ? 0 : filesByMessage[r.Id].Count());
            }

            var isAuthor = m.AuthorUserId == viewerUserId;
            result.Add(new ChatMessageDto(
                m.Id, m.FamilyId, ToAuthor(m.Author),
                m.Text, m.CreatedAt, m.EditedAt, deleted, reply,
                filesByMessage[m.Id].Select(a => new ChatAttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.PreviewStatus)).ToList(),
                reactions, links,
                CanEdit: !deleted && isAuthor,
                CanDelete: !deleted && (isAuthor || isAdmin)));
        }
        return result;
    }

    private async Task<ChatLinkPreview> ResolveLinkAsync(ChatLinkKind kind, Guid entityId, Guid viewerUserId, CancellationToken ct)
    {
        var resolver = linkResolvers.FirstOrDefault(r => r.CanResolve(kind));
        return resolver is null
            ? ChatLinkPreview.Unavailable(kind, entityId)
            : await resolver.ResolveAsync(kind, entityId, viewerUserId, ct);
    }

    private static ChatAuthorDto ToAuthor(User u) => new(u.Id, u.LastName, u.FirstName, u.MiddleName);

    private static string Snippet(string text) =>
        text.Length <= ReplySnippetLength ? text : text[..ReplySnippetLength].TrimEnd() + "…";

    // Курсор — «тики CreatedAt + id» последнего элемента страницы: (CreatedAt, Id) уникальна и монотонна.
    private static string FormatCursor(ChatMessage m) =>
        $"{m.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture)}_{m.Id:N}";

    private static (DateTime CreatedAt, Guid Id)? ParseCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        var parts = cursor.Split('_');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParseExact(parts[1], "N", out var id))
            return null;
        return (new DateTime(ticks, DateTimeKind.Utc), id);
    }

    /// <summary>Разбор поля <c>links</c> multipart-формы: JSON-массив {kind, entityId}.</summary>
    public static IReadOnlyList<ChatLinkRequest>? ParseLinks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var links = JsonSerializer.Deserialize<List<ChatLinkRequest>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return links?.All(l => Enum.IsDefined(l.Kind) && l.EntityId != Guid.Empty) == true ? links : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
