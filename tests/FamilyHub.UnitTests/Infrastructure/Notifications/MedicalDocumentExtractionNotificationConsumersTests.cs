using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.TestUtils;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Notifications;

/// <summary>
/// §2/§3 плана «живой конвейер» — MedicalDocumentExtractedNotificationConsumer/
/// MedicalDocumentExtractionFailedNotificationConsumer действительно создают строку Notification
/// (не просто компилируются). Это заодно регрессионный тест на баг, найденный при написании этих
/// самых тестов: оба consumer'а передавали FamilyId=Guid.Empty в NotifyAsync — тогда как
/// Notification.FamilyId был required NOT NULL FK на Families, и ни одной семьи с Id=Guid.Empty
/// не существует. На Postgres это означало реальный foreign_key_violation при вставке, который
/// AddIfNewAsync ловит как обычный DbUpdateException (тот же catch, что и у гонки дедупа по
/// DedupKey) и тихо проглатывает — оба уведомления никогда не создавались бы, хотя весь остальной
/// конвейер (событие ушло, consumer его получил) отрабатывал бы без единой ошибки в логах. SQLite
/// (см. SqliteTestBase) сам по себе эту проверку не проваливает — FK здесь не имитирует Postgres,
/// а просто проверяет, что после исправления (Notification.FamilyId стал Guid?, консьюмеры теперь
/// передают null) строка реально появляется и с правильными полями для клик-через (§3).
/// </summary>
public class MedicalDocumentExtractionNotificationConsumersTests : SqliteTestBase, IAsyncLifetime
{
    private readonly DomainEventTestPipeline _pipeline;

    public MedicalDocumentExtractionNotificationConsumersTests()
    {
        _pipeline = new DomainEventTestPipeline(ConnectionString, TestFieldCipher);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _pipeline.DisposeAsync();

    [Fact]
    public async Task ExtractedEvent_CreatesNotification_ForOwner_WithNullFamilyId()
    {
        var owner = Db.AddUser();
        var recordId = Guid.NewGuid();

        await _pipeline.Publisher.PublishAsync(
            new MedicalDocumentExtractedEvent(Guid.NewGuid(), recordId, owner.Id, IsDoctorVisit: false, IndicatorCount: 3, DeviationCount: 1));
        await _pipeline.DispatchAsync();

        var notification = await Db.Notifications.AsNoTracking().SingleOrDefaultAsync(n => n.UserId == owner.Id);
        notification.Should().NotBeNull(
            "если бы FamilyId всё ещё был Guid.Empty, вставка упала бы FK violation, молча проглоченным как гонка дедупа");
        notification!.Type.Should().Be(NotificationType.MedicalDocumentExtracted);
        notification.FamilyId.Should().BeNull("медзапись — персональный ресурс без семейного контекста");
        notification.RelatedEntityId.Should().Be(recordId);
        notification.RelatedEntityKind.Should().Be(NotificationRelatedKind.MedicalRecordAnalysis);
    }

    [Fact]
    public async Task ExtractionFailedEvent_CreatesNotification_ForOwner_WithDoctorVisitKind()
    {
        var owner = Db.AddUser();
        var recordId = Guid.NewGuid();

        await _pipeline.Publisher.PublishAsync(
            new MedicalDocumentExtractionFailedEvent(Guid.NewGuid(), recordId, owner.Id, IsDoctorVisit: true, "Документ нечитаем."));
        await _pipeline.DispatchAsync();

        var notification = await Db.Notifications.AsNoTracking().SingleOrDefaultAsync(n => n.UserId == owner.Id);
        notification.Should().NotBeNull(
            "терминальный отказ (не техническая недоступность LM Studio) должен породить уведомление");
        notification!.Type.Should().Be(NotificationType.MedicalDocumentExtractionFailed);
        notification.FamilyId.Should().BeNull();
        notification.RelatedEntityId.Should().Be(recordId);
        notification.RelatedEntityKind.Should().Be(NotificationRelatedKind.MedicalRecordVisit,
            "IsDoctorVisit=true на событии должен привести к клик-через на /health/visits, не /health/records");
    }
}
