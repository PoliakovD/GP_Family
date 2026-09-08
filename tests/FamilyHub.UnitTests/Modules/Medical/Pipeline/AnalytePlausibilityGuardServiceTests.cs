using System.Text.Json;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Pipeline;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Pipeline;

/// <summary>
/// Гейт «на бред» для показателей, введённых вручную (см. class doc
/// AnalytePlausibilityGuardService) — deny-by-default для любого технического сбоя самой
/// проверки, та же гарантия, что LegitimacyGuardServiceTests.
/// </summary>
public class AnalytePlausibilityGuardServiceTests
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly AnalytePlausibilityGuardService _sut;

    public AnalytePlausibilityGuardServiceTests()
    {
        _sut = new AnalytePlausibilityGuardService(
            _client, TestPromptProvider.ReturningFallback(), NullLogger<AnalytePlausibilityGuardService>.Instance);
    }

    private static Dictionary<string, JsonElement> Payload(object obj) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(obj))!;

    [Fact]
    public async Task EmptyName_IsPlausible_WithoutCallingModel()
    {
        var result = await _sut.CheckAsync("", "Кровь");

        result.IsPlausible.Should().BeTrue();
        _client.ReceivedCalls().Should().BeEmpty("пустое имя нечего проверять — экономим локальный инференс");
    }

    [Fact]
    public async Task ModelSaysValidTrue_IsPlausible()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, Payload(new { valid = true, reason = (string?)null }), null));

        var result = await _sut.CheckAsync("Гемоглобин", "Кровь");

        result.IsPlausible.Should().BeTrue();
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task ModelSaysValidFalse_IsImplausible_WithReason()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, Payload(new { valid = false, reason = "Показатель не может быть измерен в этом источнике." }), null));

        var result = await _sut.CheckAsync("Плотность мочи", "Кал");

        result.IsPlausible.Should().BeFalse();
        result.Reason.Should().Be("Показатель не может быть измерен в этом источнике.");
    }

    [Fact]
    public async Task ModelCallFails_DeniesByDefault()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(LmStudioJsonResult.Failure("Локальный сервер распознавания недоступен."));

        var result = await _sut.CheckAsync("Гемоглобин", "Кровь");

        result.IsPlausible.Should().BeFalse("техническая неудача самой проверки не должна пропускать показатель дальше");
    }

    [Fact]
    public async Task ModelResponseMissingValidField_DeniesByDefault()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, Payload(new { somethingElse = "не то поле" }), null));

        var result = await _sut.CheckAsync("Гемоглобин", "Кровь");

        result.IsPlausible.Should().BeFalse();
    }

    [Fact]
    public async Task NoSpecimenGiven_StillChecksNamePlausibility()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LmStudioJsonResult(true, Payload(new { valid = true, reason = (string?)null }), null));

        var result = await _sut.CheckAsync("Гемоглобин", null);

        result.IsPlausible.Should().BeTrue();
        _client.ReceivedCalls().Should().HaveCount(1, "имя без источника всё равно должно проверяться");
    }
}
