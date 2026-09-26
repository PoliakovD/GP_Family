using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.MedicationCourses;

/// <summary>Остаток препарата в семейной аптечке и его списание/возврат. Количество лежит строкой
/// в Medication.DataJson["quantity"] («22 таб.») — см. StockMath.</summary>
public class MedkitStockService(AppDbContext db, IFamilyAccessService access)
{
    private const string QuantityKey = "quantity";
    private const int CasAttempts = 3;

    public record StockInfo(
        Guid MedicationId, Guid FamilyId, string Name, string? MedkitName, string? QuantityText, decimal? Quantity);

    /// <summary>Препарат и его остаток; null — нет такого. Права не проверяет.</summary>
    public async Task<StockInfo?> GetAsync(Guid medicationId, CancellationToken ct = default)
    {
        var row = await db.Medications.AsNoTracking().Where(m => m.Id == medicationId)
            .Select(m => new { m.Id, m.FamilyId, m.Name, MedkitName = m.Medkit.Name, m.DataJson })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var text = ReadQuantity(row.DataJson);
        return new StockInfo(row.Id, row.FamilyId, row.Name, row.MedkitName, text,
            StockMath.TryParse(text, out var q) ? q : null);
    }

    /// <summary>Пользователь может распоряжаться остатком: активный член семьи аптечки.</summary>
    public Task<bool> CanUseAsync(Guid userId, Guid familyId, CancellationToken ct = default) =>
        access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct);

    /// <summary>
    /// Изменить остаток на <paramref name="delta"/> (минус — списание). Запись — compare-and-swap по
    /// исходному DataJson, до трёх попыток: аптечку одновременно правят люди, а «прочитал — записал»
    /// потеряло бы чужое изменение. Списание не уходит ниже нуля. Возвращает, на сколько остаток
    /// реально изменился (по модулю), либо null — если изменить нельзя (нет препарата, количество не
    /// число, гонка не разрешилась).
    /// </summary>
    public async Task<decimal?> TryAdjustAsync(Guid medicationId, decimal delta, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < CasAttempts; attempt++)
        {
            var oldJson = await db.Medications.AsNoTracking().Where(m => m.Id == medicationId)
                .Select(m => m.DataJson).FirstOrDefaultAsync(ct);
            if (oldJson is null) return null; // нет препарата или у него нет данных, а значит и количества

            var data = ParseData(oldJson);
            if (!data.TryGetValue(QuantityKey, out var text) || !StockMath.TryParse(text, out var quantity)) return null;

            var newQuantity = Math.Max(0, quantity + delta);
            var applied = Math.Abs(newQuantity - quantity);
            var newText = StockMath.ReplaceQuantity(text, newQuantity);
            if (newText is null) return null;

            data[QuantityKey] = newText;
            var newJson = JsonSerializer.Serialize(data);

            var updated = await db.Medications
                .Where(m => m.Id == medicationId && m.DataJson == oldJson)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DataJson, newJson), ct);
            if (updated == 1) return applied;
        }
        return null;
    }

    public static string? ReadQuantity(string? dataJson) =>
        ParseData(dataJson).TryGetValue(QuantityKey, out var q) ? q : null;

    private static Dictionary<string, string> ParseData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
