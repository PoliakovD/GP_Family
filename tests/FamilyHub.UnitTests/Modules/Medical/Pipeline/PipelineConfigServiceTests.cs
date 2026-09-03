using FamilyHub.Domain.Entities;
using FamilyHub.Modules.Medical.Pipeline;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Pipeline;

/// <summary>"Включён ли этот шаг" (управление enrich-пайплайном из админки, §2 плана) —
/// обязательные шаги (PipelineCatalog.IsMandatory) БД не спрашивают вовсе и не выключаются ни при
/// каких обстоятельствах; отсутствие строки для необязательного шага означает "включён" (см.
/// class doc PipelineConfigService).</summary>
public class PipelineConfigServiceTests : SqliteTestBase
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly PipelineConfigService _sut;

    public PipelineConfigServiceTests()
    {
        _sut = new PipelineConfigService(Db, _cache);
    }

    [Fact]
    public async Task IsEnabledAsync_MandatoryStep_AlwaysTrue_IgnoresAnyConfigRow()
    {
        // "extract" — обязательный шаг AnalysisExtraction (PipelineCatalog.Steps). Даже строка с
        // IsEnabled=false в БД не должна на него повлиять — обязательный шаг нельзя выключить.
        Db.PipelineStepConfigs.Add(new PipelineStepConfig
        {
            Id = Guid.NewGuid(), PipelineKey = PipelineCatalog.AnalysisExtraction, StepKey = "extract",
            IsEnabled = false, UpdatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var enabled = await _sut.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "extract");

        enabled.Should().BeTrue();
    }

    [Fact]
    public async Task IsEnabledAsync_OptionalStep_NoConfigRow_DefaultsToTrue()
    {
        var enabled = await _sut.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "ocr-correct");

        enabled.Should().BeTrue();
    }

    [Fact]
    public async Task IsEnabledAsync_OptionalStep_DisabledInDb_ReturnsFalse()
    {
        Db.PipelineStepConfigs.Add(new PipelineStepConfig
        {
            Id = Guid.NewGuid(), PipelineKey = PipelineCatalog.AnalysisExtraction, StepKey = "ocr-correct",
            IsEnabled = false, UpdatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var enabled = await _sut.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "ocr-correct");

        enabled.Should().BeFalse();
    }

    [Fact]
    public async Task IsEnabledAsync_UnknownStep_NotInCatalog_DefaultsToTrue()
    {
        var enabled = await _sut.IsEnabledAsync("unknown-pipeline", "unknown-step");

        enabled.Should().BeTrue();
    }

    [Fact]
    public async Task IsEnabledAsync_CachesResult_DoesNotSeeChangeUntilInvalidated()
    {
        (await _sut.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "ocr-correct")).Should().BeTrue();

        await using (var db2 = NewContext())
        {
            db2.PipelineStepConfigs.Add(new PipelineStepConfig
            {
                Id = Guid.NewGuid(), PipelineKey = PipelineCatalog.AnalysisExtraction, StepKey = "ocr-correct",
                IsEnabled = false, UpdatedAt = DateTime.UtcNow,
            });
            await db2.SaveChangesAsync();
        }

        (await _sut.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "ocr-correct")).Should().BeTrue("кэш ещё не инвалидирован");

        _sut.Invalidate(PipelineCatalog.AnalysisExtraction, "ocr-correct");

        (await _sut.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "ocr-correct")).Should().BeFalse();
    }
}
