namespace FamilyHub.Modules.Medical.Kb;

/// <summary>Итог каскадного поиска в справочнике — три исхода, не два: точная/алиас/уверенная нечёткая
/// привязка (<see cref="Hit"/>), неуверенная нечёткая привязка, требующая подтверждения человеком
/// (<see cref="Candidate"/>), и полный промах (<see cref="Miss"/>). Ошибочная автопривязка в медицинском
/// справочнике дороже промаха — поэтому Candidate не приравнивается к Hit.</summary>
public enum KbLookupKind { Miss, Candidate, Hit }

public sealed record KbLookupResult(KbLookupKind Kind, Guid? KbId, string? DisplayName, double Score)
{
    public static readonly KbLookupResult Miss = new(KbLookupKind.Miss, null, null, 0);

    public static KbLookupResult Hit(Guid id, string displayName, double score) =>
        new(KbLookupKind.Hit, id, displayName, score);

    public static KbLookupResult Candidate(Guid id, string displayName, double score) =>
        new(KbLookupKind.Candidate, id, displayName, score);
}

/// <summary>Проекция сырого SQL-запроса к kb.global_medications_kb (см. Search/SearchDtos.KbSearchRow).</summary>
internal sealed class KbLookupRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public double Score { get; set; }
}

/// <summary>Проекция батч-запроса точного совпадения (см. KbLookupService.LookupExactManyAsync) —
/// MatchedName несёт, КАКОЕ из входных названий совпало (в отличие от KbLookupRow, где это всегда
/// подразумевается единственным параметром запроса).</summary>
internal sealed class KbExactBatchRow
{
    public string MatchedName { get; set; } = string.Empty;
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}
