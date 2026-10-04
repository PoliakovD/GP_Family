using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Kb;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Самолечение ключей справочника показателей: приводит NormalizedName и Aliases каждой строки
/// kb.global_lab_analytes_kb к текущей форме ключа (<see cref="LabAnalyteNormalizer.RenormalizeKey"/>).
///
/// Зачем: ключ строки вычисляется один раз — при её создании. Когда нормализатор меняется (кросс-алфавитная
/// свёртка 48cd235: "treponema pallidum" → "трепонема палидум"), новые распознавания дают уже новый ключ, а
/// старая строка остаётся со старым — LabAnalyteKbLookupService не находит её ни точным ключом, ни алиасом (они
/// тоже старые), и каждый такой показатель уходит в «Одобрение» просить платный поиск того, что в справочнике
/// уже есть. LabAnalyteKbRebuildJob эту дыру не закрывал: строки с ручной правкой/проверкой он бережёт целиком,
/// включая устаревший ключ.
///
/// Идемпотентно и дёшево (справочник — сотни строк, один проход в памяти): строка, чей ключ и алиасы уже в
/// текущей форме, не трогается. Ставится на каждом старте API (RecurringJobsRegistration) и после пересборки
/// справочника. Следующее изменение формы ключа, применимое к готовому ключу, достаточно добавить в
/// RenormalizeKey — долечится само, без отдельной миграции данных.
///
/// Коллизия: новый ключ уже занят другой строкой того же биоматериала (уникальный индекс (NormalizedName,
/// SpecimenKbId)) — ключ НЕ меняется, новый ключ добавляется в алиасы (поиск начинает находить строку), а
/// объединение дублей остаётся ручным (AdminCatalogService.MergeLabAnalytesAsync) — warning в лог.
///
/// В конце — <see cref="LabAnalyteParkedKbResolver"/>: запаркованные поиски, ставшие находимыми, закрываются.
/// </summary>
[Queue("enrichment")]
public class LabAnalyteKbRekeyJob(
    AppDbContext db,
    KbChangeLogService changeLog,
    LabAnalyteParkedKbResolver parkedResolver,
    IBackgroundJobClient backgroundJobs,
    ILogger<LabAnalyteKbRekeyJob> logger)
{
    public const string ChangeLogAction = "rekey";

    /// <returns>Сколько запаркованных поисков закрыто попаданием в справочник.</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<RekeyRow>($"""
            SELECT "Id", "NormalizedName", "SpecimenKbId", "DisplayName", "Aliases" FROM kb.global_lab_analytes_kb
            """).ToListAsync(ct);

        var taken = rows.Select(r => (r.NormalizedName, r.SpecimenKbId)).ToHashSet();
        var rekeyed = 0;
        var aliasesOnly = 0;

        foreach (var row in rows)
        {
            var key = Renormalize(row.NormalizedName);
            var aliases = row.Aliases.Select(Renormalize).Where(a => a.Length > 0).ToList();
            var newName = row.NormalizedName;

            if (key != row.NormalizedName)
            {
                if (taken.Contains((key, row.SpecimenKbId)))
                {
                    aliases.Add(key);
                    logger.LogWarning(
                        "LabAnalyteKbRekeyJob: ключ «{NewKey}» строки «{DisplayName}» ({Id}) уже занят другой статьей того же " +
                        "биоматериала — ключ оставлен «{OldKey}», новый добавлен в синонимы. Дубли стоит объединить в админке.",
                        key, row.DisplayName, row.Id, row.NormalizedName);
                }
                else
                {
                    taken.Remove((row.NormalizedName, row.SpecimenKbId));
                    taken.Add((key, row.SpecimenKbId));
                    newName = key;
                }
            }

            var newAliases = aliases.Where(a => a != newName).Distinct().ToArray();
            var nameChanged = newName != row.NormalizedName;
            if (!nameChanged && newAliases.SequenceEqual(row.Aliases)) continue;

            var before = await KbRowStore.ReadLabAnalyteByIdAsync(db, row.Id, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE kb.global_lab_analytes_kb SET "NormalizedName" = {newName}, "Aliases" = {newAliases}
                WHERE "Id" = {row.Id}
                """, ct);
            var after = await KbRowStore.ReadLabAnalyteByIdAsync(db, row.Id, ct);
            await changeLog.RecordAsync(
                KbChangeTarget.LabAnalyteKb, row.Id, row.DisplayName, ChangeLogAction,
                KbChangeLogService.ToJson(before), KbChangeLogService.ToJson(after), KbChangeLogService.ActorSystem,
                nameChanged ? $"Ключ «{row.NormalizedName}» → «{newName}»" : "Синонимы приведены к текущей нормализации", ct: ct);

            if (nameChanged)
            {
                rekeyed++;
                // Показатели, распознанные с новым ключом, пока строка была ненаходима, — привязать к статье.
                backgroundJobs.Enqueue<RecalculateIndicatorFlagsJob>(j => j.RunAsync(row.Id, CancellationToken.None));
            }
            else
            {
                aliasesOnly++;
            }
        }

        var resolved = await parkedResolver.ResolveAsync(ct);
        logger.LogInformation(
            "LabAnalyteKbRekeyJob: перекейено {Rekeyed} строк, синонимы поправлены у {AliasesOnly}; " +
            "закрыто запаркованных поисков, нашедшихся в справочнике: {Resolved}.",
            rekeyed, aliasesOnly, resolved);
        return resolved;
    }

    private static string Renormalize(string key)
    {
        var normalized = LabAnalyteNormalizer.RenormalizeKey(key);
        return normalized.Length == 0 ? key : normalized;
    }

    private sealed class RekeyRow
    {
        public Guid Id { get; set; }
        public string NormalizedName { get; set; } = string.Empty;
        public Guid SpecimenKbId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string[] Aliases { get; set; } = [];
    }
}
