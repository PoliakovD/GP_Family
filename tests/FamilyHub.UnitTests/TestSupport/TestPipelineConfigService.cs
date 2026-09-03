using FamilyHub.Modules.Medical.Pipeline;
using NSubstitute;

namespace FamilyHub.UnitTests.TestSupport;

/// <summary>NSubstitute-заглушка IPipelineConfigService, всегда возвращающая "включён" — те же
/// причины, что у <see cref="TestPromptProvider"/>: в этих unit-тестах нет строк
/// PipelineStepConfig, реальный дефолт (нет строки = включён) и есть "включён".</summary>
public static class TestPipelineConfigService
{
    public static IPipelineConfigService ReturningEnabled()
    {
        var service = Substitute.For<IPipelineConfigService>();
        service.IsEnabledAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        return service;
    }
}
