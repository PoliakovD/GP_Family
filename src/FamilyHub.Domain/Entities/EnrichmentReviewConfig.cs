namespace FamilyHub.Domain.Entities;

/// <summary>
/// Единственная строка — пороги уверенности модели для ручного одобрения обогащения справочников
/// (ADR-0018). Отсутствие строки означает «значения по умолчанию» — та же конвенция, что у
/// <see cref="WebSearchConfig"/>. Четыре порога: (препараты | показатели) x (этап запроса —
/// страж легитимности/правдоподобности | этап результата — суммаризатор). Уверенность НИЖЕ порога
/// (или отсутствующая) подсвечивается в очереди поисков и принудительно отправляет черновик
/// результата на ревью вместо записи в kb.
/// </summary>
public class EnrichmentReviewConfig
{
    public const double DefaultQueryMinConfidence = 0.7;
    public const double DefaultResultMinConfidence = 0.8;

    public Guid Id { get; set; }

    public double MedicationQueryMinConfidence { get; set; } = DefaultQueryMinConfidence;

    public double AnalyteQueryMinConfidence { get; set; } = DefaultQueryMinConfidence;

    public double MedicationResultMinConfidence { get; set; } = DefaultResultMinConfidence;

    public double AnalyteResultMinConfidence { get; set; } = DefaultResultMinConfidence;

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}
