using System.Net;
using System.Net.Http.Json;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Фабрика с искусственно падающим хендлером UserLeftFamilyEvent: проверяем изоляцию сбоя
/// (остальные хендлеры выполняются), retry-учёт и dead-letter. Свой контейнер Postgres и
/// выключенный фоновый цикл — чтобы Attempts инкрементировал только dev-эндпоинт.
/// </summary>
public class OutboxFailureWebFactory : FamilyHubWebFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Только ручная доставка: фоновый OutboxDispatcher не должен гоняться
        // с dev-эндпоинтом за инкремент Attempts.
        builder.UseSetting("Outbox:PollInterval", "01:00:00");
        builder.UseSetting("Outbox:MaxAttempts", "2");
        // Без backoff-пауз: сбойная строка снова доступна следующему прогону сразу.
        builder.UseSetting("Outbox:RetryBaseDelaySeconds", "0");

        // Регистрация ПОСЛЕ AddMediatR из Program.cs → резолвится вместе со штатными хендлерами.
        builder.ConfigureServices(services =>
            services.AddTransient<INotificationHandler<UserLeftFamilyEvent>, AlwaysFailingUserLeftHandler>());
    }

    private sealed class AlwaysFailingUserLeftHandler : INotificationHandler<UserLeftFamilyEvent>
    {
        public Task Handle(UserLeftFamilyEvent notification, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Тестовый сбой хендлера UserLeftFamilyEvent");
    }
}

[CollectionDefinition(Name)]
public class OutboxFailureCollection : ICollectionFixture<OutboxFailureWebFactory>
{
    public const string Name = "OutboxFailureIntegration";
}

[Collection(OutboxFailureCollection.Name)]
public class OutboxFailureIsolationTests(OutboxFailureWebFactory factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(Guid Id, string Code);
    private record PendingMemberDto(Guid UserId);
    private record DispatchResponseDto(int Processed);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static long FreshTelegramId() => Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);

    [Fact]
    public async Task FailingHandler_DoesNotBlockOtherHandlers_AndRowDeadLettersAfterMaxAttempts()
    {
        var admin = factory.CreateClientAs(FreshTelegramId());
        var family = await (await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);
        var invite = await (await admin.PostAsJsonAsync($"/api/families/{family!.Id}/invites",
                new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 1, ExpiresAt: null)))
            .Content.ReadFromJsonAsync<CreateInviteResponseDto>(JsonOpts);
        var member = factory.CreateClientAs(FreshTelegramId());
        await member.PostAsync($"/api/invites/{invite!.Code}/redeem", null);
        var pending = await (await admin.GetAsync($"/api/families/{family.Id}/pending"))
            .Content.ReadFromJsonAsync<List<PendingMemberDto>>(JsonOpts);
        var memberUserId = pending!.Single().UserId;
        await admin.PostAsync($"/api/families/{family.Id}/members/{memberUserId}/approve", null);

        await member.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest("Пациент", DateOnly.FromDateTime(DateTime.UtcNow), null, null, null));
        (await member.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(family.Id)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await member.PostAsync($"/api/families/{family.Id}/leave", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Попытка 1: падающий хендлер не мешает Medical-чистке, строка уходит в retry.
        await DispatchAsync(admin);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        (await db.FamilyMedicalShares.AnyAsync(s => s.FamilyId == family.Id && s.OwnerUserId == memberUserId))
            .Should().BeFalse("сбой соседнего хендлера не должен блокировать отзыв шар");

        // Contains по Payload — на клиенте: LIKE по jsonb-колонке Postgres не поддерживает.
        var leftEvents = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Type == nameof(UserLeftFamilyEvent))
            .ToListAsync();
        var row = leftEvents.Single(m => m.Payload.Contains(family.Id.ToString()));
        row.ProcessedAt.Should().BeNull();
        row.Attempts.Should().Be(1);
        row.Error.Should().Contain("Тестовый сбой");

        // Попытка 2 → достигнут MaxAttempts.
        await DispatchAsync(admin);
        (await FreshRowAsync(db, row.Id)).Attempts.Should().Be(2);

        // Попытка 3: строка dead-letter — больше не выбирается, диспетчер жив.
        var processedOnThirdRun = await DispatchAsync(admin);
        processedOnThirdRun.Should().Be(0);
        var deadRow = await FreshRowAsync(db, row.Id);
        deadRow.Attempts.Should().Be(2);
        deadRow.ProcessedAt.Should().BeNull();

        // Приложение живо: обычные запросы обслуживаются.
        (await admin.GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<int> DispatchAsync(HttpClient client)
    {
        var response = await client.PostAsync("/dev/trigger-outbox-dispatch", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<DispatchResponseDto>(JsonOpts))!.Processed;
    }

    private static async Task<FamilyHub.Infrastructure.Outbox.OutboxMessage> FreshRowAsync(AppDbContext db, Guid id) =>
        await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);
}
