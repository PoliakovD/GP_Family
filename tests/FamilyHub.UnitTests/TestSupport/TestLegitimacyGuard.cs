using FamilyHub.Modules.Medical.Pipeline;
using NSubstitute;

namespace FamilyHub.UnitTests.TestSupport;

/// <summary>NSubstitute-заглушка ILegitimacyGuardService, всегда считающая текст легитимным — та
/// же причина, что у TestPromptProvider/TestPipelineConfigService: тесты, которые не проверяют
/// сам гейт, часто делят ОДИН мокнутый ILmStudioJsonClient с настоящим потребителем (например,
/// LmStudioMedicalDocumentExtractor), поэтому реальный LegitimacyGuardService против того же
/// мока получил бы чужой JSON-ответ (без поля "valid") и deny-by-default завалил бы тест
/// раньше, чем до проверяемой логики дойдёт очередь.</summary>
public static class TestLegitimacyGuard
{
    public static ILegitimacyGuardService ReturningLegitimate()
    {
        var guard = Substitute.For<ILegitimacyGuardService>();
        guard.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(LegitimacyCheckResult.Legitimate()));
        guard.CheckAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<(byte[] Bytes, string ContentType)>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(LegitimacyCheckResult.Legitimate()));
        return guard;
    }
}
