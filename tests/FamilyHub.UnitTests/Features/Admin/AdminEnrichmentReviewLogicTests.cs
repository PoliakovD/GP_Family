using System.Reflection;
using FamilyHub.Api.Features.Admin;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Kb;
using FamilyHub.TestUtils;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Features.Admin;

/// <summary>Чистая логика очереди «Одобрение» (ADR-0018): что админ реально поменял относительно черновика
/// (и, значит, что попадёт в LockedFields) и атрибуция полей к источникам.</summary>
public class AdminEnrichmentReviewLogicTests
{
    private const string DraftPayload = "{\"schemaVersion\":3,\"form\":\"таблетки\",\"purpose\":\"жаропонижающее\",\"tradeNames\":[\"Нурофен\"]}";

    private static AdminKbEditRequest? Edit(ApproveResultRequest request, string[]? aliases = null) =>
        AdminEnrichmentReviewService.BuildEdit(request, DraftPayload, "Ибупрофен", aliases ?? ["нурофен"], MedicationNameNormalizer.Normalize);

    [Fact]
    public void BuildEdit_NothingChanged_ReturnsNull_SoNoLocksAreCreated()
    {
        Edit(new ApproveResultRequest(null, null, null)).Should().BeNull();
        Edit(new ApproveResultRequest(DraftPayload, "Ибупрофен", ["Нурофен"])).Should().BeNull("то же содержимое, пробелы/регистр синонимов не в счёт");
        Edit(new ApproveResultRequest(
            "{ \"tradeNames\": [\"Нурофен\"], \"purpose\": \"жаропонижающее\", \"form\": \"таблетки\", \"schemaVersion\": 3 }", null, null))
            .Should().BeNull("порядок ключей payload не важен");
    }

    [Fact]
    public void BuildEdit_ChangedPayloadKey_LocksOnlyThatKey()
    {
        var edit = Edit(new ApproveResultRequest(
            "{\"schemaVersion\":3,\"form\":\"таблетки\",\"purpose\":\"обезболивающее\",\"tradeNames\":[\"Нурофен\"]}", null, null));

        edit.Should().NotBeNull();
        edit!.LockedPayloadKeys.Should().Equal("purpose");
        edit.PayloadJson.Should().Contain("обезболивающее");
        edit.DisplayName.Should().BeNull();
        edit.Aliases.Should().BeNull();
    }

    [Fact]
    public void BuildEdit_AddedAndRemovedKeys_AreAllCountedAsEdited()
    {
        var edit = Edit(new ApproveResultRequest(
            "{\"schemaVersion\":3,\"purpose\":\"жаропонижающее\",\"tradeNames\":[\"Нурофен\"],\"storage\":\"в сухом месте\"}", null, null));

        edit!.LockedPayloadKeys.Should().BeEquivalentTo(["form", "storage"], "удалённая форма и добавленное хранение");
    }

    [Fact]
    public void BuildEdit_NameAndAliases_AreEditedOnlyWhenTheyDiffer()
    {
        var edit = Edit(new ApproveResultRequest(null, "  Ибупрофен форте ", ["нурофен", "ибуфен"]));

        edit!.DisplayName.Should().Be("Ибупрофен форте");
        edit.Aliases.Should().Contain("ибуфен");
        edit.PayloadJson.Should().BeNull();
        edit.LockedPayloadKeys.Should().BeNull();
    }

    [Fact]
    public void BuildFieldSourceInfo_FlagsFilledFieldsWithoutSource_OnlyWhenModelReturnedAttribution()
    {
        var snippets = new[] { new DraftSnippet("A", "https://www.vidal.ru/x", "t"), new DraftSnippet("B", "https://www.rlsnet.ru/y", "t") };
        var checkedFields = new[] { "internationalName", "tradeNames", "form", "purpose", "usage" };
        var payload = "{\"internationalName\":\"Ибупрофен\",\"tradeNames\":[\"Нурофен\"],\"form\":\"таблетки\",\"purpose\":\"жаропонижающее\",\"usage\":null}";

        var withAttribution = AdminEnrichmentReviewService.BuildFieldSourceInfo(
            checkedFields, payload,
            new Dictionary<string, List<int>> { ["purpose"] = [0], ["internationalName"] = [0, 1], ["tradeNames"] = [9] },
            snippets, null);

        withAttribution.Available.Should().BeTrue();
        withAttribution.FieldSources["internationalName"].Should().Equal("https://www.vidal.ru/x", "https://www.rlsnet.ru/y");
        withAttribution.FieldSources.Should().NotContainKey("tradeNames", "индекс вне набора невалиден");
        withAttribution.FieldsWithoutSource.Should().BeEquivalentTo(["tradeNames", "form"],
            "непустые поля без источника; пустое usage не подсвечивается");

        var withoutAttribution = AdminEnrichmentReviewService.BuildFieldSourceInfo(checkedFields, payload, null, snippets, null);
        withoutAttribution.Available.Should().BeFalse();
        withoutAttribution.FieldsWithoutSource.Should().BeEmpty("модель не вернула атрибуцию (старый промпт) — подсвечивать нечего");
    }

    [Fact]
    public void BuildFieldSourceInfo_AnalyteRefRanges_TakeSourceFromRangeSourceIndex()
    {
        var snippets = new[] { new DraftSnippet("A", "https://helix.ru/a", "t") };
        var summary = new LabAnalyteSummary(
            null, "сек", "пояснение", null, null, null,
            [new LabAnalyteReferenceRange(null, null, null, 25, 35, "сек", SourceIndex: 0)], [], [0]);
        var payload = LabAnalyteKbPayload.Build(summary);

        var info = AdminEnrichmentReviewService.BuildFieldSourceInfo(
            ["defaultUnit", "plainExplanation", "refRanges"], payload,
            new Dictionary<string, List<int>> { ["plainExplanation"] = [0] }, snippets, summary);

        info.FieldSources["refRanges"].Should().Equal("https://helix.ru/a");
        info.FieldsWithoutSource.Should().Equal("defaultUnit");
    }
}

/// <summary>Освобождение отложенных задач и sweep не должны возобновлять задачи на ручном одобрении (ADR-0018):
/// единственный выход из статусов 6/7 — очередь «Одобрение».</summary>
public class AwaitingStatusesNotReleasedTests : SqliteTestBase
{
    [Fact]
    public async Task DeferredEnrichmentReleaseJob_ReleasesOnlyDeferred_NeverAwaitingAdmin()
    {
        MedicationEnrichmentJob New(string name, EnrichmentJobStatus status) => new()
        {
            Id = Guid.NewGuid(), NormalizedName = name, SourceDisplayName = name, RequestedByUserId = Guid.NewGuid(),
            FamilyId = Guid.NewGuid(), Status = status, CreatedAt = DateTime.UtcNow,
        };

        var deferred = New("а", EnrichmentJobStatus.Deferred);
        var awaitingSearch = New("б", EnrichmentJobStatus.AwaitingSearchApproval);
        var awaitingResult = New("в", EnrichmentJobStatus.AwaitingResultReview);
        Db.MedicationEnrichmentJobs.AddRange(deferred, awaitingSearch, awaitingResult);
        await Db.SaveChangesAsync();

        var backgroundJobs = Substitute.For<IBackgroundJobClient>();
        var sut = new DeferredEnrichmentReleaseJob(Db, backgroundJobs, NullLogger<DeferredEnrichmentReleaseJob>.Instance);

        await sut.RunAsync();

        Db.ChangeTracker.Clear();
        (await Db.MedicationEnrichmentJobs.SingleAsync(j => j.Id == deferred.Id)).Status.Should().Be(EnrichmentJobStatus.Pending);
        (await Db.MedicationEnrichmentJobs.SingleAsync(j => j.Id == awaitingSearch.Id)).Status.Should().Be(EnrichmentJobStatus.AwaitingSearchApproval);
        (await Db.MedicationEnrichmentJobs.SingleAsync(j => j.Id == awaitingResult.Id)).Status.Should().Be(EnrichmentJobStatus.AwaitingResultReview);
        backgroundJobs.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IBackgroundJobClient.Create))
            .Should().Be(1, "поставлена в очередь только отложенная задача");
    }
}

/// <summary>VerificationStatus — внутренний маркер админа (ADR-0018): ни один пользовательский DTO/ответ не должен его
/// содержать. Тест проходит по всем типам API и модуля Medical и разрешает поля проверки только в админских типах.</summary>
public class KbVerificationNotExposedTests
{
    private static readonly string[] Forbidden = ["VerificationStatus", "VerifiedAt", "VerifiedPayloadHash", "VerificationStale", "KbVerificationStatus"];

    private static bool IsAdminType(Type t) =>
        (t.Namespace ?? string.Empty).Contains("Features.Admin", StringComparison.Ordinal)
        || t.Name.StartsWith("Admin", StringComparison.Ordinal)
        || t.Name.StartsWith("KbChangeLog", StringComparison.Ordinal)
        || t.Name.StartsWith("KbRecordSnapshot", StringComparison.Ordinal)
        || t.Name.StartsWith("Kb", StringComparison.Ordinal) && t.Name.Contains("Verification", StringComparison.Ordinal)
        || t.Name is "KbRowStore" or "KbSnapshotRow" or "KbPayloadHash" or "ReviewCurrentKbDto";

    private static IEnumerable<Type> PublicContractTypes()
    {
        var assemblies = new[] { typeof(AdminEnrichmentReviewService).Assembly, typeof(KbCatalogService).Assembly };
        return assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .Where(t => !t.IsEnum && !t.IsAbstract || t.IsAbstract && t.IsSealed == false);
    }

    [Fact]
    public void UserFacingDtosAndEndpoints_NeverCarryVerificationFields()
    {
        var offenders = PublicContractTypes()
            .Where(t => !IsAdminType(t))
            .Where(t => !t.Name.Contains("Entity", StringComparison.Ordinal)) // EF-сущности kb живут в Domain, не здесь
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => Forbidden.Contains(p.Name) || Forbidden.Contains(p.PropertyType.Name))
                .Select(p => $"{t.FullName}.{p.Name}"))
            .ToList();

        offenders.Should().BeEmpty("статус проверки — внутренний маркер админки, пользовательские DTO его не отдают");
    }

    [Fact]
    public void PublicUserCatalogCards_DoNotExposeVerification()
    {
        // Явные публичные карточки справочника, которые видит пользователь (не путать с AdminLabAnalyteDetail/AdminMedicationDetail).
        foreach (var type in new[] { typeof(KbMedicationCard), typeof(KbAnalyteCard), typeof(KbListItem), typeof(KbAnalyteListItem), typeof(KbListResponse), typeof(KbAnalyteListResponse) })
        {
            type.GetProperties().Select(p => p.Name).Should().NotContain(Forbidden, type.Name);
        }
    }

    [Fact]
    public void AdminTypes_DoCarryVerification_SoTheCheckAboveIsMeaningful()
    {
        typeof(AdminLabAnalyteDetail).GetProperty("VerificationStatus").Should().NotBeNull();
        typeof(AdminMedicationDetail).GetProperty("VerificationStatus").Should().NotBeNull();
        typeof(AdminKbListItem).GetProperty("VerificationStatus").Should().NotBeNull();
    }
}
