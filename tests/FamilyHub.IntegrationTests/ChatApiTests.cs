using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Chat.Chat;
using FamilyHub.Modules.Chat.Realtime;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Чат семьи (ADR-0019): права, ленту, вложения, ссылки, непрочитанное, хаб и уведомления — на
/// настоящем стеке (Postgres + MinIO), через Dev-аутентификацию.</summary>
public class ChatApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    /// <summary>Минимальный валидный PNG 1×1.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(Guid Id, string Code);
    private record PendingMemberDto(Guid UserId);

    private async Task<(Guid FamilyId, HttpClient Admin, HttpClient Member)> CreateFamilyWithActiveMemberAsync()
    {
        var admin = ClientAs(FreshTelegramId());
        var family = await (await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);

        var invite = await (await admin.PostAsJsonAsync($"/api/families/{family!.Id}/invites",
                new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 1, ExpiresAt: null)))
            .Content.ReadFromJsonAsync<CreateInviteResponseDto>(JsonOpts);

        var member = ClientAs(FreshTelegramId());
        await member.PostAsync($"/api/invites/{invite!.Code}/redeem", null);
        var pending = await (await admin.GetAsync($"/api/families/{family.Id}/pending"))
            .Content.ReadFromJsonAsync<List<PendingMemberDto>>(JsonOpts);
        await admin.PostAsync($"/api/families/{family.Id}/members/{pending!.Single().UserId}/approve", null);

        return (family.Id, admin, member);
    }

    private static MultipartFormDataContent Form(
        string? text = null, Guid? replyTo = null, object? links = null, params (string Name, string ContentType, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        if (text is not null) form.Add(new StringContent(text), "text");
        if (replyTo is not null) form.Add(new StringContent(replyTo.Value.ToString()), "replyToMessageId");
        if (links is not null) form.Add(new StringContent(JsonSerializer.Serialize(links, JsonOpts)), "links");
        foreach (var (name, contentType, bytes) in files)
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "files", name);
        }
        return form;
    }

    private static async Task<ChatMessageDto> SendAsync(HttpClient client, Guid familyId, MultipartFormDataContent form)
    {
        var response = await client.PostAsync($"/api/families/{familyId}/chat/messages", form);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChatMessageDto>(JsonOpts))!;
    }

    private static async Task<ChatMessageDto> GetAsync(HttpClient client, Guid familyId, Guid messageId) =>
        (await client.GetFromJsonAsync<ChatMessageDto>($"/api/families/{familyId}/chat/messages/{messageId}", JsonOpts))!;

    [Fact]
    public async Task SendAndRead_AsMembers_OutsiderAndPendingGet403()
    {
        var (familyId, admin, member) = await CreateFamilyWithActiveMemberAsync();

        var sent = await SendAsync(member, familyId, Form("Привет, семья!"));
        sent.Text.Should().Be("Привет, семья!");
        sent.CanEdit.Should().BeTrue();

        var page = await admin.GetFromJsonAsync<ChatPageDto>($"/api/families/{familyId}/chat/messages", JsonOpts);
        page!.Items.Should().ContainSingle(m => m.Id == sent.Id && m.Text == "Привет, семья!");
        page.Items.Single().CanEdit.Should().BeFalse("править может только автор");
        page.Items.Single().CanDelete.Should().BeTrue("админ семьи модерирует");

        var outsider = ClientAs(FreshTelegramId());
        (await outsider.GetAsync($"/api/families/{familyId}/chat/messages")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.PostAsync($"/api/families/{familyId}/chat/messages", Form("чужой"))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        // Заявка ещё не одобрена — доступа нет.
        var invite = await (await admin.PostAsJsonAsync($"/api/families/{familyId}/invites",
                new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 1, ExpiresAt: null)))
            .Content.ReadFromJsonAsync<CreateInviteResponseDto>(JsonOpts);
        var pending = ClientAs(FreshTelegramId());
        await pending.PostAsync($"/api/invites/{invite!.Code}/redeem", null);
        (await pending.GetAsync($"/api/families/{familyId}/chat/messages")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EmptyOrTooLongMessage_Returns400()
    {
        var (familyId, admin, _) = await CreateFamilyWithActiveMemberAsync();

        (await admin.PostAsync($"/api/families/{familyId}/chat/messages", Form("   "))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsync($"/api/families/{familyId}/chat/messages", Form(new string('я', 4001)))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReplyReactEditDelete_RespectAuthorAndAdminRights()
    {
        var (familyId, admin, member) = await CreateFamilyWithActiveMemberAsync();
        var question = await SendAsync(admin, familyId, Form("Кто купит лекарства?"));
        var answer = await SendAsync(member, familyId, Form("Я куплю", replyTo: question.Id));

        answer.ReplyTo!.Id.Should().Be(question.Id);
        answer.ReplyTo.Snippet.Should().Be("Кто купит лекарства?");

        // Реакции: идемпотентно, «моя» видна только себе, произвольный эмодзи не принимается.
        var emoji = Uri.EscapeDataString("👍");
        (await admin.PutAsync($"/api/families/{familyId}/chat/messages/{answer.Id}/reactions/{emoji}", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.PutAsync($"/api/families/{familyId}/chat/messages/{answer.Id}/reactions/{emoji}", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.PutAsync($"/api/families/{familyId}/chat/messages/{answer.Id}/reactions/{Uri.EscapeDataString("🤡")}", null))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var reacted = await GetAsync(member, familyId, answer.Id);
        reacted.Reactions.Should().ContainSingle(r => r.Emoji == "👍" && r.Count == 1 && !r.Mine);
        (await GetAsync(admin, familyId, answer.Id)).Reactions.Single().Mine.Should().BeTrue();

        // Править — только автор.
        (await admin.PatchAsJsonAsync($"/api/families/{familyId}/chat/messages/{answer.Id}", new { text = "чужая правка" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PatchAsJsonAsync($"/api/families/{familyId}/chat/messages/{answer.Id}", new { text = "Я куплю вечером" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var edited = await GetAsync(admin, familyId, answer.Id);
        edited.Text.Should().Be("Я куплю вечером");
        edited.EditedAt.Should().NotBeNull();

        // Удалять чужое — только админ; участник не может удалить сообщение админа.
        (await member.DeleteAsync($"/api/families/{familyId}/chat/messages/{question.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.DeleteAsync($"/api/families/{familyId}/chat/messages/{answer.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var deleted = await GetAsync(member, familyId, answer.Id);
        deleted.Deleted.Should().BeTrue();
        deleted.Text.Should().BeEmpty();
        deleted.Reactions.Should().BeEmpty();
        deleted.CanDelete.Should().BeFalse();
    }

    [Fact]
    public async Task Attachments_VisibleToMembersOnly_AndRemovedWithMessage()
    {
        var (familyId, admin, member) = await CreateFamilyWithActiveMemberAsync();
        var sent = await SendAsync(member, familyId, Form("Фото рецепта",
            files: [("рецепт.png", "image/png", Png), ("справка.pdf", "application/pdf", Encoding.UTF8.GetBytes("%PDF-1.4 test"))]));

        sent.Attachments.Should().HaveCount(2);
        var attachmentId = sent.Attachments.First().Id;

        (await admin.GetAsync($"/api/attachments/{attachmentId}/preview")).StatusCode.Should().Be(HttpStatusCode.OK);
        var outsider = ClientAs(FreshTelegramId());
        (await outsider.GetAsync($"/api/attachments/{attachmentId}/preview")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await member.DeleteAsync($"/api/families/{familyId}/chat/messages/{sent.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.GetAsync($"/api/attachments/{attachmentId}/preview")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Attachments_SeveralFilesAboveGlobalBodyLimit_AreAccepted()
    {
        // Глобальный лимит тела Kestrel рассчитан на одно мед-вложение (5 + 5 МБ); у отправки сообщения свой,
        // из лимитов чата (6 × 5 МБ), через IRequestSizeLimitMetadata эндпоинта. TestServer лимит Kestrel не
        // применяет, поэтому здесь проверяются лимиты чата и multipart-формы (RequestFormLimits), а сам
        // подъём лимита Kestrel — вручную на настоящем хосте (см. .claude/research/family-chat.md).
        var (familyId, admin, _) = await CreateFamilyWithActiveMemberAsync();
        var big = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(big);

        var sent = await SendAsync(admin, familyId, Form(null,
            files: [("a.pdf", "application/pdf", big), ("b.pdf", "application/pdf", big), ("c.pdf", "application/pdf", big)]));

        sent.Attachments.Should().HaveCount(3);
    }

    [Fact]
    public async Task Links_ResolvedPerViewer_AndOnlyToWhatAuthorSees()
    {
        var (familyId, admin, member) = await CreateFamilyWithActiveMemberAsync();
        var record = (await (await member.PostAsJsonAsync("/api/medical-records",
                new CreateMedicalRecordRequest(DateOnly.FromDateTime(DateTime.UtcNow), "Терапевт", "Описание", null)))
            .Content.ReadFromJsonAsync<MedicalRecordDto>(JsonOpts))!;

        var sent = await SendAsync(member, familyId, Form("Мой анализ",
            links: new[] { new { kind = (int)ChatLinkKind.MedicalRecord, entityId = record.Id } }));

        var forAuthor = sent.Links.Single();
        forAuthor.Accessible.Should().BeTrue();
        forAuthor.Title.Should().NotBeNullOrEmpty();
        forAuthor.RouteKind.Should().NotBeNull();

        // Запись не расшарена семье — админ видит карточку без заголовка.
        var forAdmin = (await GetAsync(admin, familyId, sent.Id)).Links.Single();
        forAdmin.Accessible.Should().BeFalse();
        forAdmin.Title.Should().BeNull();
        forAdmin.Subtitle.Should().BeNull();

        // Сослаться на запись, которой сам не видишь, нельзя.
        (await admin.PostAsync($"/api/families/{familyId}/chat/messages",
                Form("смотрите", links: new[] { new { kind = (int)ChatLinkKind.MedicalRecord, entityId = record.Id } })))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unread_CountsOthersMessages_UntilMarkedRead()
    {
        var (familyId, admin, member) = await CreateFamilyWithActiveMemberAsync();
        await SendAsync(admin, familyId, Form("свое не считается"));
        await SendAsync(member, familyId, Form("раз"));
        await SendAsync(member, familyId, Form("два"));

        var unread = await admin.GetFromJsonAsync<List<ChatUnreadDto>>("/api/chat/unread", JsonOpts);
        unread!.Single(u => u.FamilyId == familyId).Count.Should().Be(2);

        (await admin.PostAsJsonAsync($"/api/families/{familyId}/chat/read", new { })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        unread = await admin.GetFromJsonAsync<List<ChatUnreadDto>>("/api/chat/unread", JsonOpts);
        unread!.Single(u => u.FamilyId == familyId).Count.Should().Be(0);

        // Отправка своего сообщения тоже отмечает прочитанным.
        (await member.GetFromJsonAsync<List<ChatUnreadDto>>("/api/chat/unread", JsonOpts))!
            .Single(u => u.FamilyId == familyId).Count.Should().Be(0);
    }

    [Fact]
    public async Task Paging_ByCursor_WalksWholeHistoryNewestFirst()
    {
        var (familyId, admin, _) = await CreateFamilyWithActiveMemberAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add((await SendAsync(admin, familyId, Form($"#{i}"))).Id);

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/api/families/{familyId}/chat/messages?limit=2" + (cursor is null ? "" : $"&before={cursor}");
            var page = (await admin.GetFromJsonAsync<ChatPageDto>(url, JsonOpts))!;
            seen.AddRange(page.Items.Select(m => m.Id));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null && pages < 10);

        pages.Should().Be(3);
        seen.Should().Equal(Enumerable.Reverse(ids));

        (await admin.GetAsync($"/api/families/{familyId}/chat/messages?before=garbage")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Hub_WithTicket_ReceivesIdOnlyEvents_WithoutTicket_IsRejected()
    {
        var (familyId, admin, member) = await CreateFamilyWithActiveMemberAsync();

        var ticketResponse = await admin.PostAsync("/api/chat/hub-ticket", null);
        ticketResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var ticket = (await ticketResponse.Content.ReadFromJsonAsync<HubTicketDto>(JsonOpts))!.Ticket;

        await using var connection = BuildHubConnection(ticket);
        var received = new TaskCompletionSource<ChatMessageChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<ChatMessageChangedEvent>(ChatEvents.MessageChangedMethod, e =>
        {
            if (e.FamilyId == familyId) received.TrySetResult(e);
        });
        await connection.StartAsync();
        // StartAsync возвращается после рукопожатия, а подписка на группы семей — в OnConnectedAsync сервера,
        // уже после него (с запросом в БД). Вызовы хаба сервер обрабатывает только после OnConnectedAsync,
        // поэтому холостой StopViewing — барьер: дальше соединение точно в группе, событие не потеряется.
        await connection.InvokeAsync(nameof(ChatHub.StopViewing));

        var sent = await SendAsync(member, familyId, Form("realtime"));

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        evt.MessageId.Should().Be(sent.Id);
        evt.Change.Should().Be(ChatChange.Created);

        // Без тикета (и с испорченным) — хаб не пускает.
        await using var anonymous = BuildHubConnection(null);
        await FluentActions.Awaiting(() => anonymous.StartAsync()).Should().ThrowAsync<Exception>();
        await using var forged = BuildHubConnection(ticket[..^4] + "0000");
        await FluentActions.Awaiting(() => forged.StartAsync()).Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task NotifyJob_NotifiesOthersWithoutText_OncePerWindow()
    {
        var (familyId, admin, member) = await CreateFamilyWithActiveMemberAsync();
        var first = await SendAsync(member, familyId, Form("секретный текст про давление"));
        var second = await SendAsync(member, familyId, Form("ещё одно"));

        using (var scope = Factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<ChatMessageNotifyJob>();
            await job.RunAsync(first.Id, CancellationToken.None);
            await job.RunAsync(second.Id, CancellationToken.None);
        }

        using var check = Factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var notifications = await db.Notifications.AsNoTracking()
            .Where(n => n.FamilyId == familyId && n.Type == NotificationType.FamilyChatMessage)
            .ToListAsync();

        // Автору — ничего; админу — одно (второе сообщение попало в то же 10-минутное окно).
        notifications.Should().ContainSingle();
        var notification = notifications.Single();
        notification.UserId.Should().NotBe(first.Author.UserId);
        notification.RelatedEntityId.Should().Be(familyId);
        notification.RelatedEntityKind.Should().Be(NotificationRelatedKind.FamilyChat);
        notification.Body.Should().NotContain("давление");
    }

    [Fact]
    public async Task DeleteFamily_RemovesChatAttachments()
    {
        var (familyId, admin, member) = await CreateFamilyWithActiveMemberAsync();
        var sent = await SendAsync(member, familyId, Form(null, files: [("x.png", "image/png", Png)]));

        (await admin.DeleteAsync($"/api/families/{familyId}")).EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.FileAttachments.AnyAsync(a => a.OwnerType == FileOwnerType.ChatMessage && a.OwnerId == sent.Id))
            .Should().BeFalse();
        (await db.ChatMessages.AnyAsync(m => m.Id == sent.Id)).Should().BeFalse();
    }

    private HubConnection BuildHubConnection(string? ticket)
    {
        var server = Factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/chat"), options =>
            {
                // TestServer не поднимает настоящих сокетов — long polling поверх его in-memory handler'а.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                if (ticket is not null) options.AccessTokenProvider = () => Task.FromResult<string?>(ticket);
            })
            .Build();
    }
}
