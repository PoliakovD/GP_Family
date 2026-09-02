using FamilyHub.Domain.Entities;
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
/// пайплайна анализов) — ключ теперь (показатель, источник), не одно имя: специфичная по источнику
/// запись должна побеждать обобщённую (SpecimenKbId=SpecimenContextIds.Unresolved), а промах под
/// конкретный источник должен откатываться на обобщённое знание. Реальный Postgres нужен по той же
/// причине, что и в KbLookupTests — raw-SQL пороги (similarity/tsvector) недоступны на SQLite.
/// </summary>
public class LabAnalyteKbLookupTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    // Источник — ссылка на GlobalSpecimenKb, не enum (пересборка enrich-пайплайна); произвольные
    // Guid'ы здесь просто отличают "кровь" от "мочи" для теста каскада, без реальных строк
    // справочника источников (эти тесты проверяют LabAnalyteKbLookupService, не GlobalSpecimenKb).
    private static readonly Guid BloodId = Guid.NewGuid();
    private static readonly Guid UrineId = Guid.NewGuid();

    private async Task SeedKbAsync(string normalizedName, Guid specimenKbId, string displayName)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        db.GlobalLabAnalytesKb.Add(new GlobalLabAnalyteKb
        {
            Id = Guid.NewGuid(),
            NormalizedName = normalizedName,
            SpecimenKbId = specimenKbId,
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
        await SeedKbAsync(name, SpecimenContextIds.Unresolved, "Белок (общий)");
        await SeedKbAsync(name, UrineId, "Белок в моче");

        var sut = CreateSut(out var scope);
        using (scope)
        {
            var result = await sut.LookupAsync(name, UrineId);

            result.Kind.Should().Be(KbLookupKind.Hit);
            result.DisplayName.Should().Be("Белок в моче",
                "специфичная по источнику запись должна побеждать обобщённую (Unresolved)");
        }
    }

    [Fact]
    public async Task NoSpecificSpecimenRecord_FallsBackToUnknown()
    {
        var name = $"глюкоза{Guid.NewGuid():N}";
        await SeedKbAsync(name, SpecimenContextIds.Unresolved, "Глюкоза (общая)");

        var sut = CreateSut(out var scope);
        using (scope)
        {
            var result = await sut.LookupAsync(name, BloodId);

            result.Kind.Should().Be(KbLookupKind.Hit,
                "промах под конкретный источник должен откатываться на обобщённое знание (Unresolved)");
            result.DisplayName.Should().Be("Глюкоза (общая)");
        }
    }

    [Fact]
    public async Task DifferentSpecimen_DoesNotMatchUnrelatedRecord()
    {
        var name = $"лейкоциты{Guid.NewGuid():N}";
        await SeedKbAsync(name, BloodId, "Лейкоциты крови");

        var sut = CreateSut(out var scope);
        using (scope)
        {
            var result = await sut.LookupAsync(name, UrineId);

            result.Kind.Should().Be(KbLookupKind.Miss,
                "запись под другой источник (кровь) не должна закрывать промах для мочи без явного Unresolved-фолбэка");
        }
    }
}
