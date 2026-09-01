using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Kb;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Каскадный поиск в справочнике показателей (LabAnalyteKbLookupService, пересборка enrich-
/// пайплайна анализов) — ключ теперь (показатель, биоматериал), не одно имя: специфичная по
/// биоматериалу запись должна побеждать обобщённую (Specimen=Unknown), а промах под конкретный
/// биоматериал должен откатываться на обобщённое знание. Реальный Postgres нужен по той же
/// причине, что и в KbLookupTests — raw-SQL пороги (similarity/tsvector) недоступны на SQLite.
/// </summary>
public class LabAnalyteKbLookupTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private async Task SeedKbAsync(string normalizedName, SpecimenType specimen, string displayName)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        db.GlobalLabAnalytesKb.Add(new GlobalLabAnalyteKb
        {
            Id = Guid.NewGuid(),
            NormalizedName = normalizedName,
            Specimen = specimen,
            DisplayName = displayName,
            PayloadJson = """{"schemaVersion":4}""",
            Source = "тест",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private LabAnalyteKbLookupService CreateSut(out IServiceScope scope)
    {
        scope = Factory.Services.CreateScope();
        return new LabAnalyteKbLookupService(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task SpecificSpecimenRecord_PreferredOverUnknownFallback()
    {
        var name = $"белок{Guid.NewGuid():N}";
        await SeedKbAsync(name, SpecimenType.Unknown, "Белок (общий)");
        await SeedKbAsync(name, SpecimenType.Urine, "Белок в моче");

        var sut = CreateSut(out var scope);
        using (scope)
        {
            var result = await sut.LookupAsync(name, SpecimenType.Urine);

            result.Kind.Should().Be(KbLookupKind.Hit);
            result.DisplayName.Should().Be("Белок в моче",
                "специфичная по биоматериалу запись должна побеждать обобщённую (Unknown)");
        }
    }

    [Fact]
    public async Task NoSpecificSpecimenRecord_FallsBackToUnknown()
    {
        var name = $"глюкоза{Guid.NewGuid():N}";
        await SeedKbAsync(name, SpecimenType.Unknown, "Глюкоза (общая)");

        var sut = CreateSut(out var scope);
        using (scope)
        {
            var result = await sut.LookupAsync(name, SpecimenType.Blood);

            result.Kind.Should().Be(KbLookupKind.Hit,
                "промах под конкретный биоматериал должен откатываться на обобщённое знание (Specimen=Unknown)");
            result.DisplayName.Should().Be("Глюкоза (общая)");
        }
    }

    [Fact]
    public async Task DifferentSpecimen_DoesNotMatchUnrelatedRecord()
    {
        var name = $"лейкоциты{Guid.NewGuid():N}";
        await SeedKbAsync(name, SpecimenType.Blood, "Лейкоциты крови");

        var sut = CreateSut(out var scope);
        using (scope)
        {
            var result = await sut.LookupAsync(name, SpecimenType.Urine);

            result.Kind.Should().Be(KbLookupKind.Miss,
                "запись под другой биоматериал (кровь) не должна закрывать промах для мочи без явного Unknown-фолбэка");
        }
    }
}
