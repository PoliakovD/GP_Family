using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Короткое название анализа (заметка 4, см. class doc AnalysisTitleGenerator).</summary>
public class AnalysisTitleGeneratorTests
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly AnalysisTitleGenerator _sut;

    public AnalysisTitleGeneratorTests()
    {
        _sut = new AnalysisTitleGenerator(_client, TestPromptProvider.ReturningFallback(), NullLogger<AnalysisTitleGenerator>.Instance);
    }

    private static DocumentContent TextContent(string text) => DocumentContent.FromText(text);

    [Fact]
    public async Task GenerateAsync_ModelUnavailable_ReturnsNull_DoesNotThrow()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(LmStudioJsonResult.Failure("недоступен"));

        var result = await _sut.GenerateAsync(TextContent("бланк"), ["Гемоглобин"]);

        result.Should().BeNull();
    }

    /// <summary>Регрессия TECH_DEBT.md #5 — см. QualitativeNormJudgeTests.JudgeAsync_TransientFailure_Throws.</summary>
    [Fact]
    public async Task GenerateAsync_TransientFailure_Throws()
    {
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(LmStudioJsonResult.Failure("Локальный сервер распознавания недоступен.", isTransient: true));

        var act = () => _sut.GenerateAsync(TextContent("бланк"), ["Гемоглобин"]);

        await act.Should().ThrowAsync<LmStudioUnavailableException>();
    }
}
