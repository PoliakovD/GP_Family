using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Contracts.Messaging;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using MassTransit;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Искусственно падающий потребитель UserLeftFamilyEvent — независимый (третий) consumer group
/// на топике user-left-family, зарегистрированный через Messaging:ExtraConsumerAssemblies (см.
/// KafkaWebFactory.ConfigureWebHost) на ВСЮ Kafka-коллекцию. Проверяет тот же инвариант, что
/// MessagingFailureIsolationTests для InMemory-топологии, но на реальной Kafka (ADR-0007):
/// падение одной consumer group не блокирует соседние (см. FailingConsumerGroup_...).
/// </summary>
public sealed class AlwaysFailingUserLeftFamilyKafkaConsumer : IConsumer<UserLeftFamilyEvent>
{
    public static int Attempts;

    public Task Consume(ConsumeContext<UserLeftFamilyEvent> context)
    {
        Interlocked.Increment(ref Attempts);
        throw new InvalidOperationException("Тестовый сбой Kafka-потребителя UserLeftFamilyEvent");
    }
}

[CollectionDefinition(Name)]
public class KafkaIntegrationCollection : ICollectionFixture<KafkaWebFactory>
{
    public const string Name = "KafkaIntegration";
}

/// <summary>
/// Единственная коллекция с Messaging:Kafka:Enabled=true (KafkaWebFactory) — round-trip через
/// реальный брокер (Testcontainers.Kafka), а не только через MassTransit-харнесс. С ADR-0007
/// это единственное место, которое реально проверяет прод-топологию: бизнес-потребители
/// (Notifications, Medical cleanup) подписаны на Kafka Rider TopicEndpoint, а не на InMemory —
/// у Kafka Rider нет in-memory тестового харнесса, поэтому только здесь можно проверить, что
/// правильные потребители реально подписаны на правильные топики/consumer group, что два
/// потребителя одного топика (UserLeftFamilyEvent) не мешают друг другу, и что падение одного
/// потребителя не блокирует независимый consumer group. Юнит-тесты (DomainEventTestPipeline,
/// ConsumerFailureIsolationTests) проверяют то же самое, но только на dev-lite InMemory-ветке.
/// </summary>
[Collection(KafkaIntegrationCollection.Name)]
public class KafkaBridgeFlowTests(KafkaWebFactory factory) : IAsyncDisposable
{
    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(Guid Id, string Code);
    private record PendingMemberDto(Guid UserId);
    private record NotificationItemDto(Guid Id, NotificationType Type, string Title);
    private record ConsentVersionDto(string Version);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static long FreshTelegramId() => Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);

    private readonly List<IConsumer<Ignore, string>> _consumers = [];

    private static void AcceptCurrentConsent(HttpClient client)
    {
        var current = client.GetFromJsonAsync<ConsentVersionDto>("/api/consents/current", JsonOpts)
            .GetAwaiter().GetResult();
        client.PostAsJsonAsync("/api/consents/accept", new { version = current!.Version })
            .GetAwaiter().GetResult().EnsureSuccessStatusCode();
    }

    private async Task<List<NotificationItemDto>> GetNotificationsAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/notifications")).Content.ReadFromJsonAsync<List<NotificationItemDto>>(JsonOpts))!;

    private static async Task WaitForAsync(Func<Task<bool>> condition, string because, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }

        (await condition()).Should().BeTrue(because);
    }

    /// <summary>
    /// Сырой consumer, подписанный на топик заранее (до действия, публикующего событие) —
    /// AutoOffsetReset.Latest иначе прочитал бы сообщения прошлых прогонов этого набора тестов.
    /// </summary>
    private async Task<IConsumer<Ignore, string>> SubscribeFromEndAsync(string topic)
    {
        // Топик ещё не существует (KafkaTopicBridgeConsumer ничего в него не публиковал) —
        // явно создаём заранее, а не полагаемся на auto.create.topics.enable + первый Produce:
        // Subscribe к несуществующему топику бросает ConsumeException ("Unknown topic or
        // partition") на первом Consume, а не молча ждёт его появления.
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = factory.BootstrapServers }).Build())
        {
            try
            {
                await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
            }
            catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
            {
                // Другой тест этой же коллекции уже создал топик раньше — не ошибка.
            }
        }

        var consumer = new ConsumerBuilder<Ignore, string>(new ConsumerConfig
        {
            BootstrapServers = factory.BootstrapServers,
            GroupId = $"kafka-bridge-flow-tests-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Latest,
        }).Build();
        consumer.Subscribe(topic);
        _consumers.Add(consumer);

        // Ждём назначения партиций явно (а не полагаемся на таймаут первого Consume в тесте) —
        // без этого Latest-офсет мог бы разрешиться уже ПОСЛЕ публикации события ниже по тесту
        // и пропустить его. Действие, публикующее событие, всегда выполняется уже после этого
        // вызова — поэтому забрать здесь чужое сообщение физически невозможно.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && consumer.Assignment.Count == 0)
        {
            try { consumer.Consume(TimeSpan.FromMilliseconds(200)); }
            catch (ConsumeException) { /* метаданные топика ещё не разошлись по брокеру — повторим */ }
        }
        consumer.Assignment.Should().NotBeEmpty("consumer должен получить назначение партиций до начала теста");

        return consumer;
    }

    private static string? PollForMessage(IConsumer<Ignore, string> consumer, string containing, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ConsumeResult<Ignore, string>? result;
            try { result = consumer.Consume(TimeSpan.FromMilliseconds(300)); }
            catch (ConsumeException) { continue; }
            if (result?.Message?.Value?.Contains(containing) == true)
                return result.Message.Value;
        }

        return null;
    }

    [Fact]
    public async Task MemberApproved_ReachesKafkaTopic_AndIsConsumedByRealNotificationConsumer()
    {
        var kafkaConsumer = await SubscribeFromEndAsync(KafkaTopics.MemberApproved);

        var admin = factory.CreateClientAs(FreshTelegramId());
        AcceptCurrentConsent(admin);
        var family = await (await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);
        var invite = await (await admin.PostAsJsonAsync($"/api/families/{family!.Id}/invites",
                new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 1, ExpiresAt: null)))
            .Content.ReadFromJsonAsync<CreateInviteResponseDto>(JsonOpts);
        var member = factory.CreateClientAs(FreshTelegramId());
        AcceptCurrentConsent(member);
        await member.PostAsync($"/api/invites/{invite!.Code}/redeem", null);
        var pending = await (await admin.GetAsync($"/api/families/{family.Id}/pending"))
            .Content.ReadFromJsonAsync<List<PendingMemberDto>>(JsonOpts);
        var memberUserId = pending!.Single().UserId;

        (await admin.PostAsync($"/api/families/{family.Id}/members/{memberUserId}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // С ADR-0007 это уже НЕ параллельный путь поверх InMemory-доставки — это ЕДИНСТВЕННЫЙ
        // путь: MemberApprovedNotificationConsumer подписан на Kafka Rider TopicEndpoint, оповещение
        // появляется только если реальная Kafka-топология (топик/consumer group) работает верно.
        await WaitForAsync(async () =>
            (await GetNotificationsAsync(admin)).Any(n => n.Type == NotificationType.MemberApproved),
            "MemberApprovedNotificationConsumer, подписанный на Kafka-топик member-approved, должен доставить оповещение");

        var payload = PollForMessage(kafkaConsumer, family.Id.ToString(), timeoutMs: 15_000);
        payload.Should().NotBeNull("MemberApprovedEvent должен дойти до Kafka-топика member-approved через KafkaTopicBridgeConsumer");
        payload.Should().Contain(memberUserId.ToString());

        // Один approve — одно сообщение: повторный опрос того же топика не должен ничего найти.
        var duplicate = PollForMessage(kafkaConsumer, family.Id.ToString(), timeoutMs: 2_000);
        duplicate.Should().BeNull("approve вызывался ровно один раз — повторного сообщения на топике быть не должно");
    }

    /// <summary>Семья + активный участник, у которого есть расшаренная на семью мед-запись —
    /// общий сетап для сценариев вокруг UserLeftFamilyEvent (два независимых потребителя).</summary>
    private async Task<(Guid FamilyId, HttpClient Admin, HttpClient Member)> CreateFamilyWithSharedMedicalRecordAsync()
    {
        var admin = factory.CreateClientAs(FreshTelegramId());
        AcceptCurrentConsent(admin);
        var family = await (await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);
        var invite = await (await admin.PostAsJsonAsync($"/api/families/{family!.Id}/invites",
                new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 1, ExpiresAt: null)))
            .Content.ReadFromJsonAsync<CreateInviteResponseDto>(JsonOpts);
        var member = factory.CreateClientAs(FreshTelegramId());
        AcceptCurrentConsent(member);
        await member.PostAsync($"/api/invites/{invite!.Code}/redeem", null);
        var pending = await (await admin.GetAsync($"/api/families/{family.Id}/pending"))
            .Content.ReadFromJsonAsync<List<PendingMemberDto>>(JsonOpts);
        var memberUserId = pending!.Single().UserId;
        await admin.PostAsync($"/api/families/{family.Id}/members/{memberUserId}/approve", null);

        await member.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest("Пациент", DateOnly.FromDateTime(DateTime.UtcNow), null, null, null));
        (await member.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(family.Id)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var sharesBefore = await (await member.GetAsync("/api/medical-records/shares")).Content.ReadFromJsonAsync<List<Guid>>(JsonOpts);
        sharesBefore.Should().Contain(family.Id);

        return (family.Id, admin, member);
    }

    /// <summary>
    /// UserLeftFamilyEvent — единственное событие с двумя потребителями. На Kafka это два
    /// НЕЗАВИСИМЫХ consumer group на одном топике (notifications-user-left-family,
    /// medical-user-left-family) — то, что раньше страховал ConcurrentMessageLimit/один InMemory
    /// receive endpoint (см. DomainEventTestPipeline), теперь целиком держится на изоляции
    /// consumer group. Проверяем против реального брокера, не только доверяя документации.
    /// </summary>
    [Fact]
    public async Task UserLeftFamily_BothConsumersProcessIndependently()
    {
        var (familyId, admin, member) = await CreateFamilyWithSharedMedicalRecordAsync();

        (await member.PostAsync($"/api/families/{familyId}/leave", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Обе группы читают одно и то же сообщение из одного топика независимо — если бы они
        // конкурировали за партиции (одна group на двоих), одна из проверок ниже зависла бы.
        await WaitForAsync(async () =>
        {
            var shares = await (await member.GetAsync("/api/medical-records/shares")).Content.ReadFromJsonAsync<List<Guid>>(JsonOpts);
            return !shares!.Contains(familyId);
        }, "medical-user-left-family consumer group (UserLeftFamilyMedicalCleanupConsumer) должна отозвать шару");

        await WaitForAsync(async () =>
            (await GetNotificationsAsync(admin)).Any(n => n.Type == NotificationType.MemberLeft),
            "notifications-user-left-family consumer group (UserLeftFamilyNotificationConsumer) должна доставить оповещение админу");
    }

    /// <summary>
    /// AlwaysFailingUserLeftFamilyKafkaConsumer — третья consumer group на топике
    /// user-left-family, всегда включённая на этой фабрике (см. KafkaWebFactory) — падает при
    /// КАЖДОМ прогоне коллекции, не только в этом тесте; здесь просто явно проверяем, что её
    /// ретраи исчерпываются (не растут бесконечно) и что две штатные группы её не замечают.
    /// </summary>
    [Fact]
    public async Task FailingConsumerGroup_OnKafka_DoesNotBlockTheTwoRealConsumerGroups()
    {
        var (familyId, admin, member) = await CreateFamilyWithSharedMedicalRecordAsync();

        var attemptsBefore = AlwaysFailingUserLeftFamilyKafkaConsumer.Attempts;
        (await member.PostAsync($"/api/families/{familyId}/leave", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Падающая группа реально получила сообщение и исчерпала ретраи (1 + RetryLimit=2 — см.
        // KafkaWebFactory) — не просто была пропущена.
        await WaitForAsync(
            () => Task.FromResult(AlwaysFailingUserLeftFamilyKafkaConsumer.Attempts - attemptsBefore >= 3),
            "падающая Kafka consumer group должна исчерпать все попытки (1 + RetryLimit)");

        await WaitForAsync(async () =>
        {
            var shares = await (await member.GetAsync("/api/medical-records/shares")).Content.ReadFromJsonAsync<List<Guid>>(JsonOpts);
            return !shares!.Contains(familyId);
        }, "medical-user-left-family consumer group должна отработать несмотря на падение соседней группы");

        await WaitForAsync(async () =>
            (await GetNotificationsAsync(admin)).Any(n => n.Type == NotificationType.MemberLeft),
            "notifications-user-left-family consumer group должна отработать несмотря на падение соседней группы");

        // Приложение живо: обычные запросы обслуживаются.
        (await admin.GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var consumer in _consumers)
        {
            consumer.Close();
            consumer.Dispose();
        }

        await Task.CompletedTask;
    }
}
