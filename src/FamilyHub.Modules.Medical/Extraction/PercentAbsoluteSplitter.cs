using FamilyHub.Infrastructure.Search;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Лейкоцитарная формула печатает каждую клетку дважды — в процентах и в абсолютном количестве — и
/// нередко ОДНИМ И ТЕМ ЖЕ названием: "Нейтрофилы | 55 | %" и "Нейтрофилы | 3.1 | 10^9/л". Маркера
/// абсолютной формы в имени нет, поэтому LabAnalyteNormalizer.NormalizeAnalyteKey даёт обеим один ключ,
/// и вторая строка того же бланка перезаписала бы первую (upsert по уникальному ключу записи). Здесь —
/// разводка по единицам в пределах ОДНОГО файла: если у строк с одним ключом есть и процентная, и
/// непроцентная единица, к имени непроцентных дописывается ", абс." — дальше ключ ("… абс"), поиск в
/// справочнике, отображение и пересборка справочника (LabAnalyteKbRebuildJob пересчитывает ключ из
/// сохранённого имени) работают как для бланка, где "абс." напечатано явно. Процентная строка не
/// меняется — её ключ остаётся прежним.
/// </summary>
public static class PercentAbsoluteSplitter
{
    public const string AbsoluteNameSuffix = ", абс.";

    public static List<(ExtractedLabIndicator Dto, Guid FileGroupId)> Apply(
        IReadOnlyList<(ExtractedLabIndicator Dto, Guid FileGroupId)> indicators)
    {
        var result = indicators.ToList();
        var groups = result
            .Select((x, i) => (Index: i, Key: LabAnalyteNormalizer.NormalizeAnalyteKey(x.Dto.Name), x.FileGroupId, IsPercent: IsPercent(x.Dto)))
            .Where(x => x.Key.Length > 0)
            .GroupBy(x => (x.Key, x.FileGroupId));

        foreach (var group in groups)
        {
            var members = group.ToList();
            if (members.Count < 2 || !members.Any(m => m.IsPercent) || members.All(m => m.IsPercent)) continue;

            foreach (var member in members.Where(m => !m.IsPercent && !string.IsNullOrWhiteSpace(result[m.Index].Dto.Unit)))
            {
                var (dto, fileGroupId) = result[member.Index];
                result[member.Index] = (dto with { Name = dto.Name.TrimEnd() + AbsoluteNameSuffix }, fileGroupId);
            }
        }
        return result;
    }

    /// <summary>Процентная форма: единица "%" или "%" в конце названия ("Нейтрофилы (общ.число), %").</summary>
    private static bool IsPercent(ExtractedLabIndicator dto) =>
        dto.Unit?.Trim() == "%" || dto.Name.TrimEnd().EndsWith('%');
}
