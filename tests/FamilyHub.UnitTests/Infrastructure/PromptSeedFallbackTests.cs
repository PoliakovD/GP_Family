using System.Reflection;
using FamilyHub.Infrastructure.Migrations;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Pipeline;
using FamilyHub.Modules.Medical.Vaccinations;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure;

/// <summary>Версия 1 слотов промптов, засеянная миграцией, должна совпадать с фолбэком в коде слово в слово, а сами
/// слоты — быть в каталоге (иначе промпт нельзя ни увидеть, ни поправить из админки).</summary>
public class PromptSeedFallbackTests
{
    private static string Const(Type type, string name) =>
        (string)type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    [Fact]
    public void CacheUnitsAndVaccinationOcr_SeedEqualsCodeFallback()
    {
        var migration = typeof(AddCacheUnitsAndVaccinationOcrPrompts);

        Const(migration, "CacheUnitsBody").Should().Be(Const(typeof(LabAnalyteCacheUnitsBackfillJob), "Prompt"));
        Const(migration, "VaccinationOcrBody").Should().Be(Const(typeof(VaccinationCertificateOcrService), "SystemPrompt"));
    }

    [Theory]
    [InlineData("analysis.cache-units")]
    [InlineData("vaccination.certificate-ocr")]
    public void Slot_IsRegisteredInCatalog(string key) =>
        PromptCatalog.Prompts.Should().Contain(p => p.Key == key);
}
