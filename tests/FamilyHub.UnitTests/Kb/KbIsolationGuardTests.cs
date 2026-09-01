using System.Text.RegularExpressions;
using FamilyHub.Domain.Entities;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyHub.UnitTests.Kb;

/// <summary>
/// Guard-тесты изоляции кэша знаний (задача 2.6): в схему kb не может попасть персональный
/// контекст. Проверяется рефлексией по EF-модели, поэтому любая будущая kb-таблица
/// автоматически подпадает под инвариант.
/// </summary>
public class KbIsolationGuardTests : SqliteTestBase
{
    private static readonly Regex PersonalContextPattern = new(
        "(UserId|FamilyId|Person|Owner|Telegram|Email|Phone|Member)",
        RegexOptions.IgnoreCase);

    [Fact]
    public void AllKbSchemaEntities_HaveNoPersonalContext()
    {
        var kbEntities = Db.Model.GetEntityTypes()
            .Where(e => e.GetSchema() == "kb")
            .ToList();

        kbEntities.Should().NotBeEmpty("справочник знаний существует в схеме kb");

        foreach (var entity in kbEntities)
        {
            entity.GetProperties()
                .Where(p => PersonalContextPattern.IsMatch(p.Name))
                .Should().BeEmpty($"в kb-таблице {entity.GetTableName()} не должно быть персональных полей");

            entity.GetForeignKeys().Should().BeEmpty(
                $"kb-таблица {entity.GetTableName()} не должна ссылаться на другие таблицы");
            entity.GetNavigations().Should().BeEmpty(
                $"kb-таблица {entity.GetTableName()} не должна иметь навигаций");
        }
    }

    [Fact]
    public void GlobalMedicationKb_LivesInKbSchema_WithUniqueNormalizedName()
    {
        var entity = Db.Model.FindEntityType(typeof(GlobalMedicationKb))!;

        entity.GetSchema().Should().Be("kb");
        entity.GetTableName().Should().Be("global_medications_kb");
        entity.GetIndexes().Should().Contain(i =>
            i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == nameof(GlobalMedicationKb.NormalizedName));
    }

    [Fact]
    public void GlobalLabAnalyteKb_LivesInKbSchema_WithUniqueNormalizedNameAndSpecimen()
    {
        // Ветка medicalrecords: второй kb-writer (LabAnalyteKbWriter) — тот же инвариант,
        // что у справочника медикаментов, проверенный отдельно на случай, если общий
        // reflection-тест выше когда-нибудь ослабят. Ключ дедупликации — пара (показатель,
        // биоматериал), не одно имя (пересборка enrich-пайплайна) — см. GlobalLabAnalyteKb.Specimen.
        var entity = Db.Model.FindEntityType(typeof(GlobalLabAnalyteKb))!;

        entity.GetSchema().Should().Be("kb");
        entity.GetTableName().Should().Be("global_lab_analytes_kb");
        entity.GetIndexes().Should().Contain(i =>
            i.IsUnique && i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == nameof(GlobalLabAnalyteKb.NormalizedName)) &&
            i.Properties.Any(p => p.Name == nameof(GlobalLabAnalyteKb.Specimen)));
    }

    [Fact]
    public void LabAnalyteSearchCache_LivesInKbSchema_WithUniqueNormalizedNameAndSpecimen()
    {
        // Кэш платного поиска для анализов (пересборка enrich-пайплайна, зеркало
        // MedicationSearchCache) — тот же инвариант изоляции, проверенный отдельно.
        var entity = Db.Model.FindEntityType(typeof(LabAnalyteSearchCache))!;

        entity.GetSchema().Should().Be("kb");
        entity.GetTableName().Should().Be("lab_analyte_search_cache");
        entity.GetIndexes().Should().Contain(i =>
            i.IsUnique && i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == nameof(LabAnalyteSearchCache.NormalizedName)) &&
            i.Properties.Any(p => p.Name == nameof(LabAnalyteSearchCache.Specimen)));
    }

    [Fact]
    public void GlobalSpecimenKb_LivesInKbSchema_WithUniqueNormalizedName()
    {
        // Общий справочник биоматериалов вне SpecimenType (пересборка enrich-пайплайна,
        // GlobalSpecimenKbService) — тот же инвариант изоляции, проверенный отдельно.
        var entity = Db.Model.FindEntityType(typeof(GlobalSpecimenKb))!;

        entity.GetSchema().Should().Be("kb");
        entity.GetTableName().Should().Be("global_specimens_kb");
        entity.GetIndexes().Should().Contain(i =>
            i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == nameof(GlobalSpecimenKb.NormalizedName));
    }

    [Fact]
    public void PersonalCompatibilityResult_IsUserBound_AndLivesInMedicalSchema()
    {
        var entity = Db.Model.FindEntityType(typeof(PersonalCompatibilityResult))!;

        entity.GetSchema().Should().Be("medical", "персональные результаты — медицинские данные, не kb");
        entity.FindProperty(nameof(PersonalCompatibilityResult.UserId))!
            .IsNullable.Should().BeFalse("результат всегда привязан к пользователю");
    }

    [Fact]
    public async Task PersonalContext_CannotBeStoredInKbRow()
    {
        // Смоук на уровне данных: попытка записать в kb payload с user-Guid'ом должна
        // отлавливаться будущим writer-сервисом этапа 4; здесь фиксируем сам инвариант
        // структуры — kb-строка не имеет полей, куда можно положить владельца.
        Db.GlobalMedicationsKb.Add(new GlobalMedicationKb
        {
            Id = Guid.NewGuid(),
            NormalizedName = "аспирин",
            DisplayName = "Аспирин",
            PayloadJson = """{"activeSubstance":"АСК"}""",
            Source = "тест",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var row = Db.GlobalMedicationsKb.AsNoTracking().Single();
        typeof(GlobalMedicationKb).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(name => PersonalContextPattern.IsMatch(name));
        row.NormalizedName.Should().Be("аспирин");
    }
}
