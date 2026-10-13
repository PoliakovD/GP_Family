using FamilyHub.Infrastructure.Auth;
using FamilyHub.Infrastructure.CurrentUser;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FamilyHub.Modules.Chat.Chat;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();
        var chat = group.MapGroup("/families/{familyId:guid}/chat");

        group.MapGet("/chat/config", (ChatService service) => Results.Ok(service.GetConfig()));

        group.MapGet("/chat/unread", async (ChatService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetUnreadAsync(currentUser.UserId, ct)));

        // Тикет входа в хаб (ADR-0019 п. 3) — POST: под CSRF-гейтом для PWA и под консент-фильтром модуля.
        group.MapPost("/chat/hub-ticket", (HubTicketService tickets, ICurrentUser currentUser) =>
        {
            var (ticket, expiresAt) = tickets.Create(currentUser.UserId);
            return Results.Ok(new HubTicketDto(ticket, expiresAt));
        });

        chat.MapGet("/messages", async (
            Guid familyId, string? before, int? limit, ChatService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, page) = await service.GetPageAsync(familyId, currentUser.UserId, before, limit, ct);
            return result switch
            {
                ChatResult.Forbidden => Results.Forbid(),
                ChatResult.Invalid => Results.BadRequest(new { code = "invalid_cursor" }),
                _ => Results.Ok(page),
            };
        });

        chat.MapGet("/messages/{messageId:guid}", async (
            Guid familyId, Guid messageId, ChatService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.GetMessageAsync(familyId, messageId, currentUser.UserId, ct);
            return ToResult(result, item);
        });

        // Все файлы сообщения — одним multipart-запросом, поэтому лимит тела — свой у эндпоинта, из лимитов чата,
        // а не глобальный Kestrel (тот рассчитан на одно мед-вложение, KestrelLimitsRegistration).
        // .DisableAntiforgery() — по той же причине, что у загрузки мед-вложений (см. AttachmentEndpoints):
        // CSRF проверяет глобальный гейт по заголовку, встроенный antiforgery-middleware не подключён.
        var chatOptions = app.ServiceProvider.GetRequiredService<IOptions<ChatOptions>>().Value;
        var maxBody = chatOptions.MaxFilesPerMessage * chatOptions.MaxFileSizeBytes + 1024 * 1024;
        chat.MapPost("/messages", async (
            Guid familyId,
            [FromForm] string? text,
            [FromForm] Guid? replyToMessageId,
            [FromForm] string? links,
            IFormFileCollection? files,
            ChatService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var parsedLinks = ChatService.ParseLinks(links);
            if (parsedLinks is null) return Results.BadRequest(new { code = "invalid_links", error = "Некорректные ссылки." });

            var uploads = (files ?? new FormFileCollection())
                .Select(f => new ChatUpload(f.FileName, f.ContentType, f.Length, f.OpenReadStream))
                .ToList();
            var (result, item, error) = await service.SendAsync(
                familyId, currentUser.UserId, new SendChatMessageInput(text, replyToMessageId, parsedLinks, uploads), ct);
            return result switch
            {
                ChatResult.Forbidden => Results.Forbid(),
                ChatResult.Invalid => Results.BadRequest(new { code = "invalid_message", error }),
                _ => Results.Created($"/api/families/{familyId}/chat/messages/{item!.Id}", item),
            };
        })
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(maxBody))
            .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = maxBody })
            .RequireRateLimiting("chat-write");

        chat.MapPatch("/messages/{messageId:guid}", async (
            Guid familyId, Guid messageId, EditChatMessageRequest request, ChatService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, error) = await service.EditAsync(familyId, messageId, currentUser.UserId, request.Text, ct);
            return result switch
            {
                ChatResult.NotFound => Results.NotFound(),
                ChatResult.Forbidden => Results.Forbid(),
                ChatResult.Invalid => Results.BadRequest(new { code = "invalid_message", error }),
                _ => Results.NoContent(),
            };
        }).RequireRateLimiting("chat-write");

        chat.MapDelete("/messages/{messageId:guid}", async (
            Guid familyId, Guid messageId, ChatService service, ICurrentUser currentUser, CancellationToken ct) =>
            ToResult(await service.DeleteAsync(familyId, messageId, currentUser.UserId, ct)));

        // Эмодзи — сегментом пути (фронт кодирует encodeURIComponent); набор фиксирован, см. ChatService.Reactions.
        chat.MapPut("/messages/{messageId:guid}/reactions/{emoji}", async (
            Guid familyId, Guid messageId, string emoji, ChatService service, ICurrentUser currentUser, CancellationToken ct) =>
            ToResult(await service.SetReactionAsync(familyId, messageId, currentUser.UserId, Uri.UnescapeDataString(emoji), true, ct)))
            .RequireRateLimiting("chat-write");

        chat.MapDelete("/messages/{messageId:guid}/reactions/{emoji}", async (
            Guid familyId, Guid messageId, string emoji, ChatService service, ICurrentUser currentUser, CancellationToken ct) =>
            ToResult(await service.SetReactionAsync(familyId, messageId, currentUser.UserId, Uri.UnescapeDataString(emoji), false, ct)))
            .RequireRateLimiting("chat-write");

        chat.MapPost("/read", async (
            Guid familyId, MarkChatReadRequest? request, ChatService service, ICurrentUser currentUser, CancellationToken ct) =>
            ToResult(await service.MarkReadAsync(familyId, currentUser.UserId, request?.UpTo, ct)));
    }

    private static IResult ToResult(ChatResult result) => result switch
    {
        ChatResult.NotFound => Results.NotFound(),
        ChatResult.Forbidden => Results.Forbid(),
        ChatResult.Invalid => Results.BadRequest(),
        _ => Results.NoContent(),
    };

    private static IResult ToResult<T>(ChatResult result, T? item) => result switch
    {
        ChatResult.NotFound => Results.NotFound(),
        ChatResult.Forbidden => Results.Forbid(),
        ChatResult.Invalid => Results.BadRequest(),
        _ => Results.Ok(item),
    };
}
