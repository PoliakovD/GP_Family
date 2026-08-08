using System.Net;
using System.Net.Http.Json;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Искусственно падающий потребитель UserLeftFamilyEvent, зарегистрированный через
/// Messaging:ExtraConsumerAssemblies (см. MessagingOptions/MassTransitRegistration) — seam,
/// заменивший невозможный второй AddMassTransit. Живёт в этой же сборке — тестовому хосту
/// достаточно знать её имя, чтобы AddConsumers его подхватил вместе со штатными потребителями.
/// Seam работает только в InMemory-ветке регистрации (Messaging:Kafka:Enabled=false, дефолт
/// FamilyHubWebFactory, эта фабрика его не переопределяет) — при Enabled=true бизнес-потребители
/// уходят на Kafka Rider с явным списком типов (ADR-0007), AddConsumers там не вызывается.
/// </summary>
public sealed class AlwaysFailingUserLeftFamilyConsumer : IConsumer<UserLeftFamilyEvent>
{
    public static int Attempts;

    public Task Consume(ConsumeContext<UserLeftFamilyEvent> context)
    {
        Interlocked.Increment(ref Attempts);
        throw new InvalidOperationException("Тестовый сбой потребителя UserLeftFamilyEvent");
    }
}

/// <summary>
/// Фабрика с искусственно падающим потребителем UserLeftFamilyEvent: проверяем изоляцию сбоя
/// (сосед-потребитель — Medical-чистка — всё равно отрабатывает) и что ретрай падающего
/// потребителя действительно исчерпывается, а не растёт бесконечно (ADR-0006 — топология
/// MassTransit даёт эту изоляцию сама, без IsolatingLoggingPublisher). Retry сжат до
/// миллисекунд (Messaging:Retry:*) — тест ждёт настоящего исчерпания попыток, не подделывая его,
/// но не тратит на это реальные секунды exponential backoff прод-настроек.
///
/// Проверяет InMemory/dev-lite-топологию (Messaging:Kafka:Enabled=false) — прод-путь через
/// Kafka Rider проверяет отдельный аналог этого теста в KafkaIntegrationCollection (ADR-0007).
/// </summary>
public class MessagingFailureWebFactory : FamilyHubWebFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("Messaging:ExtraConsumerAssemblies", typeof(AlwaysFailingUserLeftFamilyConsumer).Assembly.GetName().Name);
        builder.UseSetting("Messaging:Retry:RetryLimit", "2");
        builder.UseSetting("Messaging:Retry:MinInterval", "00:00:00.050");
        builder.UseSetting("Messaging:Retry:MaxInterval", "00:00:00.200");
        builder.UseSetting("Messaging:Retry:IntervalDelta", "00:00:00.050");
    }
}

[CollectionDefinition(Name)]
public class MessagingFailureCollection : ICollectionFixture<MessagingFailureWebFactory>
{
    public const string Name = "MessagingFailureIntegration";
}

[Collection(MessagingFailureCollection.Name)]
public class MessagingFailureIsolationTests(MessagingFailureWebFactory factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(Guid Id, string Code);
    private record PendingMemberDto(Guid UserId);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static long FreshTelegramId() => Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);

    private static async Task WaitForAsync(Func<Task<bool>> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        (await condition()).Should().BeTrue(because);
    }

    [Fact]
    public async Task FailingConsumer_DoesNotBlockNeighbor_AndRetryCapsOutInsteadOfGrowingForever()
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

        // Медицинские эндпоинты закрыты консент-фильтром (задача 2.3) — принимаем согласие.
        var consent = await (await member.GetAsync("/api/consents/current"))
            .Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>(JsonOpts);
        (await member.PostAsJsonAsync("/api/consents/accept", new { version = consent!["version"].GetString() }))
            .EnsureSuccessStatusCode();

        await member.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest("Пациент", DateOnly.FromDateTime(DateTime.UtcNow), null, null, null));
        (await member.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(family.Id)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var attemptsBefore = AlwaysFailingUserLeftFamilyConsumer.Attempts;
        (await member.PostAsync($"/api/families/{family.Id}/leave", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Сосед по этому же событию (Medical-чистка) — свой receive endpoint, падение
        // AlwaysFailingUserLeftFamilyConsumer на СВОЁМ endpoint его не блокирует и не задерживает.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await WaitForAsync(
            async () => !await db.FamilyMedicalShares.AsNoTracking()
                .AnyAsync(s => s.FamilyId == family.Id && s.OwnerUserId == memberUserId),
            "сбой соседнего потребителя не должен блокировать отзыв шар");

        // Ретрай падающего потребителя действительно исчерпывается (RetryLimit=2 → 1 попытка +
        // 2 ретрая = 3 вызова Consume), а не продолжает расти бесконечно.
        await WaitForAsync(
            () => Task.FromResult(AlwaysFailingUserLeftFamilyConsumer.Attempts - attemptsBefore >= 3),
            "падающий потребитель должен исчерпать все попытки (1 + RetryLimit)");
        var attemptsAtExhaustion = AlwaysFailingUserLeftFamilyConsumer.Attempts;
        await Task.Delay(500); // грейс-период — ловит гипотетический бесконечный ретрай
        AlwaysFailingUserLeftFamilyConsumer.Attempts.Should().Be(attemptsAtExhaustion,
            "после исчерпания лимита счётчик попыток не должен расти дальше");

        // EF Core Outbox всё равно опустел для этого прогона — доставка НА ШИНУ (не потребителю)
        // от сбоя соседнего потребителя не зависит: то, что забрал OutboxDispatcher, покидает
        // таблицу сразу после успешной публикации.
        await WaitForAsync(
            async () => await db.Database
                .SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM \"OutboxMessage\"")
                .SingleAsync() == 0,
            "outbox должен опустеть — сбой потребителя не блокирует доставку на шину");

        // Приложение живо: обычные запросы обслуживаются.
        (await admin.GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
