using FamilyHub.Modules.Medical.Pipeline;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Pipeline;

/// <summary>
/// Инвариант из требования "проверка легитимности/prompt injection первым шагом каждого
/// конвейера, нельзя выключить": единственный способ гарантировать, что новый пайплайн не забудет
/// про этот шаг, — проверить это на уровне самого реестра, а не полагаться на ручную дисциплину
/// при объявлении PipelineCatalog.Steps.
/// </summary>
public class PipelineCatalogTests
{
    private static readonly string[] AllPipelineKeys =
    [
        PipelineCatalog.AnalysisExtraction, PipelineCatalog.VisitExtraction,
        PipelineCatalog.LabAnalyteEnrichment, PipelineCatalog.MedicationEnrichment,
    ];

    [Theory]
    [MemberData(nameof(PipelineKeys))]
    public void EveryPipeline_HasMandatoryLegitimacyCheckStep(string pipelineKey)
    {
        var step = PipelineCatalog.Find(pipelineKey, PipelineCatalog.LegitimacyCheckStep);

        step.Should().NotBeNull($"пайплайн {pipelineKey} обязан начинаться с проверки легитимности");
        step!.IsMandatory.Should().BeTrue("проверку легитимности нельзя выключить ни для одного пайплайна");
        step.PromptKey.Should().Be("guard.legitimacy-check");
    }

    public static IEnumerable<object[]> PipelineKeys() => AllPipelineKeys.Select(k => new object[] { k });
}
