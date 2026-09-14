using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Одноразовый перепрогон (план "нормы из бланка: односторонние референсы и качественные
/// результаты") — показатели, распознанные ДО фикса каскада IndicatorFlagCalculator, застряли на
/// Flag.Unknown навсегда: RefSource.Blank (референс из бланка) в приоритете каскада и никогда не
/// переопределяется справочником, значит и не подхватывался RecalculateIndicatorFlagsJob (тот
/// пересчитывает только None/Inferred). POST /api/admin/pipeline/recompute-indicator-flags чинит
/// эти застрявшие записи прямым повторным прогоном Calculate по уже сохранённым данным.
/// </summary>
[Collection(AdminIntegrationCollection.Name)]
public class RecomputeIndicatorFlagsBackfillJobTests(AdminWebFactory factory)
{
    private static readonly Guid OwnerUserId = Guid.NewGuid();

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }))
            .EnsureSuccessStatusCode();
        return client;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string because, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }

        (await condition()).Should().BeTrue(because);
    }

    [Fact]
    public async Task Recompute_IndicatorStuckWithOneSidedRefText_BecomesNormalWithParsedBounds()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var recordId = Guid.NewGuid();
        db.MedicalRecords.Add(new MedicalRecord
        {
            Id = recordId, OwnerUserId = OwnerUserId, Kind = MedicalRecordKind.Analysis,
            RecordDate = new DateOnly(2026, 1, 1), ExtractionStatus = ExtractionStatus.Ready, CreatedAt = DateTime.UtcNow,
        });

        // Записан ДО фикса: "<47" уехало целиком в RefText, ничего не распарсило одностороннюю
        // границу — застряло на (Unknown, Blank) навсегда (Blank в приоритете, RecalculateIndicatorFlagsJob
        // его не трогает).
        var indicatorId = Guid.NewGuid();
        db.LabIndicators.Add(new LabIndicator
        {
            Id = indicatorId, MedicalRecordId = recordId, RecordDate = new DateOnly(2026, 1, 1), OwnerUserId = OwnerUserId,
            AnalyteKey = "тестовыйпоказатель", DisplayName = "Тестовый показатель", Position = 0,
            ValueRaw = "12", Flag = IndicatorFlag.Unknown, RefSource = RefSource.Blank, RefText = "<47",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var admin = await AdminClientAsync();
        var response = await admin.PostAsync("/api/admin/pipeline/recompute-indicator-flags", null);
        response.EnsureSuccessStatusCode();

        await WaitForAsync(async () =>
        {
            using var pollScope = factory.Services.CreateScope();
            var pollDb = pollScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var indicator = await pollDb.LabIndicators.AsNoTracking().SingleAsync(i => i.Id == indicatorId);
            return indicator.Flag != IndicatorFlag.Unknown;
        }, "перепрогон должен распарсить \"<47\" и пересчитать флаг");

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await verifyDb.LabIndicators.AsNoTracking().SingleAsync(i => i.Id == indicatorId);

        after.Flag.Should().Be(IndicatorFlag.Normal, "12 внутри распарсенного диапазона 0-47");
        after.RefSource.Should().Be(RefSource.Blank, "референс всё ещё из самого бланка, просто теперь корректно распарсен");
        after.RefLowText.Should().Be("0");
        after.RefHighText.Should().Be("47");
        after.RefText.Should().Be("<47", "сырой текст референса не переписывается — пользователь видит исходную формулировку");
    }

    [Fact]
    public async Task Recompute_IndicatorWithHonestlyUnknownReference_StaysUnknown()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var recordId = Guid.NewGuid();
        db.MedicalRecords.Add(new MedicalRecord
        {
            Id = recordId, OwnerUserId = OwnerUserId, Kind = MedicalRecordKind.Analysis,
            RecordDate = new DateOnly(2026, 1, 1), ExtractionStatus = ExtractionStatus.Ready, CreatedAt = DateTime.UtcNow,
        });

        var indicatorId = Guid.NewGuid();
        db.LabIndicators.Add(new LabIndicator
        {
            Id = indicatorId, MedicalRecordId = recordId, RecordDate = new DateOnly(2026, 1, 1), OwnerUserId = OwnerUserId,
            AnalyteKey = "загадочныйпоказатель", DisplayName = "Загадочный показатель", Position = 0,
            ValueRaw = "42", Flag = IndicatorFlag.Unknown, RefSource = RefSource.None,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var admin = await AdminClientAsync();
        var response = await admin.PostAsync("/api/admin/pipeline/recompute-indicator-flags", null);
        response.EnsureSuccessStatusCode();

        // Ждать конкретное изменение бессмысленно — его не будет; ждём завершения самой задачи
        // через отдельный маркер (второй показатель того же прогона), затем проверяем, что этот
        // остался нетронутым. Здесь достаточно фиксированной паузы — задача сама по себе быстрая
        // (один показатель без KB), а WaitForAsync-условие для "ничего не изменилось" не годится.
        await Task.Delay(2000);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await verifyDb.LabIndicators.AsNoTracking().SingleAsync(i => i.Id == indicatorId);

        after.Flag.Should().Be(IndicatorFlag.Unknown, "нет ни бланка, ни KB, ни модельной догадки — честный Unknown остаётся честным Unknown");
        after.RefSource.Should().Be(RefSource.None);
    }
}
