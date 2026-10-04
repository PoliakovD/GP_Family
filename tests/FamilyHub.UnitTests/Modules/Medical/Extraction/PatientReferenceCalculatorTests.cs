using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Шаг 3 каскада референса (RefSource.KbCalculated, см. class doc PatientReferenceCalculator).</summary>
public class PatientReferenceCalculatorTests
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly PatientReferenceCalculator _sut;

    public PatientReferenceCalculatorTests()
    {
        _sut = new PatientReferenceCalculator(_client, TestPromptProvider.ReturningFallback(), NullLogger<PatientReferenceCalculator>.Instance);
    }

    [Fact]
    public async Task CalculateAsync_ModelUnavailable_ReturnsNull_DoesNotThrow()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(LmStudioJsonResult.Failure("недоступен"));

        var result = await _sut.CalculateAsync("Клиренс креатинина", "методика", 30, null, "мл/мин");

        result.Should().BeNull();
    }

    /// <summary>Регрессия TECH_DEBT.md #5 — см. QualitativeNormJudgeTests.JudgeAsync_TransientFailure_Throws.</summary>
    [Fact]
    public async Task CalculateAsync_TransientFailure_Throws()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(LmStudioJsonResult.Failure("Локальный сервер распознавания недоступен.", isTransient: true));

        var act = () => _sut.CalculateAsync("Клиренс креатинина", "методика", 30, null, "мл/мин");

        await act.Should().ThrowAsync<LmStudioUnavailableException>();
    }
}
