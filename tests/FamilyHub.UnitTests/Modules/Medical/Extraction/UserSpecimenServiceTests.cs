using System.Text.Json;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>UX-редизайн: пользовательский справочник биоматериалов, провалидированный LLM один
/// раз при создании (см. class doc UserSpecimenService) — приём "модель предлагает,
/// детерминированный код ветирует", тот же, что MedicationEnrichmentProcessor.ResolveCorrectedName.</summary>
public class UserSpecimenServiceTests : SqliteTestBase
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly UserSpecimenService _sut;

    public UserSpecimenServiceTests()
    {
        // LLM-гейт вынесен в GlobalSpecimenKbService (пересборка enrich-пайплайна) — тот же мок
        // клиента, реальный сервис поверх него, чтобы поведение UserSpecimenService проверялось
        // сквозь настоящую логику гейта/детерминированного вето, а не через второй мок.
        var globalKb = new GlobalSpecimenKbService(Db, _client, NullLogger<GlobalSpecimenKbService>.Instance);
        _sut = new UserSpecimenService(Db, globalKb, NullLogger<UserSpecimenService>.Instance);
    }

    private void SetUpModelResponse(bool valid, string? displayName, string? reason = null)
    {
        var payload = new Dictionary<string, JsonElement>
        {
            ["valid"] = JsonSerializer.SerializeToElement(valid),
        };
        if (displayName is not null) payload["displayName"] = JsonSerializer.SerializeToElement(displayName);
        if (reason is not null) payload["reason"] = JsonSerializer.SerializeToElement(reason);

        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, payload, null));
    }

    [Fact]
    public async Task CreateAsync_TooShortOrInvalidChars_RejectedWithoutCallingModel()
    {
        var owner = Db.AddUser();

        var (result, _, _) = await _sut.CreateAsync(owner.Id, "a1");

        result.Should().Be(CreateSpecimenResult.InvalidInput);
        await _client.DidNotReceiveWithAnyArgs().ExtractJsonAsync(default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_DuplicateOfOwnExisting_ReturnsAlreadyExists_WithoutCallingModel()
    {
        var owner = Db.AddUser();
        SetUpModelResponse(valid: true, displayName: "Ликвор (СМЖ)");
        var (first, _, _) = await _sut.CreateAsync(owner.Id, "ликвор");
        first.Should().Be(CreateSpecimenResult.Success);
        _client.ClearReceivedCalls();

        var (result, item, _) = await _sut.CreateAsync(owner.Id, "Ликвор");

        result.Should().Be(CreateSpecimenResult.AlreadyExists);
        item!.DisplayName.Should().Be("Ликвор (СМЖ)");
        await _client.DidNotReceiveWithAnyArgs().ExtractJsonAsync(default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_ModelRejects_ReturnsRejectedWithReason()
    {
        var owner = Db.AddUser();
        SetUpModelResponse(valid: false, displayName: null, reason: "Это не биоматериал.");

        var (result, item, reason) = await _sut.CreateAsync(owner.Id, "гемоглобин");

        result.Should().Be(CreateSpecimenResult.Rejected);
        item.Should().BeNull();
        reason.Should().Be("Это не биоматериал.");
    }

    [Fact]
    public async Task CreateAsync_ModelSubstitutesUnrelatedConcept_VetoedByTrigramSimilarity()
    {
        // Модель "поправляет" явно другое понятие — не орфографию, а подмену; должно быть отклонено
        // детерминированным вето (см. MinValiditySimilarity), даже если модель сказала valid=true.
        var owner = Db.AddUser();
        SetUpModelResponse(valid: true, displayName: "Совершенно другое слово");

        var (result, item, _) = await _sut.CreateAsync(owner.Id, "ликвор");

        result.Should().Be(CreateSpecimenResult.Rejected);
        item.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_LmStudioUnavailable_ReturnsUnavailable_DoesNotPersist()
    {
        var owner = Db.AddUser();
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(LmStudioJsonResult.Failure("timeout"));

        var (result, item, _) = await _sut.CreateAsync(owner.Id, "ликвор");

        result.Should().Be(CreateSpecimenResult.Unavailable);
        item.Should().BeNull();
        (await _sut.GetOwnAsync(owner.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ValidNewBiomaterial_PersistsAndReturnsSuccess()
    {
        var owner = Db.AddUser();
        SetUpModelResponse(valid: true, displayName: "Мокрота");

        var (result, item, _) = await _sut.CreateAsync(owner.Id, "мокрота");

        result.Should().Be(CreateSpecimenResult.Success);
        item!.DisplayName.Should().Be("Мокрота");
        (await _sut.GetOwnAsync(owner.Id)).Should().ContainSingle(s => s.SpecimenKbId == item.SpecimenKbId);
    }

    [Fact]
    public async Task CreateAsync_DifferentOwners_DoNotConflictOnSameName()
    {
        var ownerA = Db.AddUser();
        var ownerB = Db.AddUser();
        SetUpModelResponse(valid: true, displayName: "Мокрота");

        var (resultA, _, _) = await _sut.CreateAsync(ownerA.Id, "мокрота");
        var (resultB, _, _) = await _sut.CreateAsync(ownerB.Id, "мокрота");

        resultA.Should().Be(CreateSpecimenResult.Success);
        resultB.Should().Be(CreateSpecimenResult.Success);
    }

    [Fact]
    public async Task CreateAsync_SameNameAlreadyValidatedByAnotherOwner_ReusesGlobalKb_WithoutSecondLlmCall()
    {
        // Пересборка enrich-пайплайна (B7): второй пользователь, вводящий то же самое название,
        // не должен тратить второй LLM-вызов — общий справочник уже провалидировал это слово.
        var ownerA = Db.AddUser();
        var ownerB = Db.AddUser();
        SetUpModelResponse(valid: true, displayName: "Ликвор (СМЖ)");

        var (resultA, itemA, _) = await _sut.CreateAsync(ownerA.Id, "ликвор");
        resultA.Should().Be(CreateSpecimenResult.Success);
        itemA!.DisplayName.Should().Be("Ликвор (СМЖ)");
        _client.ClearReceivedCalls();

        var (resultB, itemB, _) = await _sut.CreateAsync(ownerB.Id, "Ликвор");

        resultB.Should().Be(CreateSpecimenResult.Success);
        itemB!.DisplayName.Should().Be("Ликвор (СМЖ)", "написание должно взяться из общего справочника, не заново от модели");
        itemB.SpecimenKbId.Should().Be(itemA.SpecimenKbId, "оба владельца ссылаются на одну и ту же запись общего справочника");
        await _client.DidNotReceiveWithAnyArgs().ExtractJsonAsync(default!, default!, Arg.Any<CancellationToken>());
    }
}
