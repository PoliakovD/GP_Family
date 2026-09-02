namespace FamilyHub.Domain.Entities;

/// <summary>
/// Известные строки <see cref="GlobalSpecimenKb"/>, на которые код ссылается напрямую — это НЕ
/// классификация (та целиком в данных, пересборка enrich-пайплайна: источник показателя — будь
/// то биоматериал "кровь" или исследование "ЭКГ" — всегда обычная строка справочника, LLM
/// нормализует, код только сверяет по триграмме, см. SpecimenResolver), а стабильная ссылка на
/// ОДНУ конкретную системную запись. Тот же по духу приём, что <c>SystemUserId = Guid.Empty</c> у
/// системных задач (см. LabAnalyteKbReenrichJob).
/// </summary>
public static class SpecimenContextIds
{
    /// <summary>Засеивается миграцией под этим фиксированным Id — строка "Не определено" в
    /// GlobalSpecimenKb. <c>LabIndicator.SpecimenKbId</c> указывает сюда, когда SpecimenResolver
    /// не смог уверенно определить источник показателя (confidence ниже порога, либо источник не
    /// упомянут в документе вовсе). Обогащение справочника для этой ссылки НИКОГДА не ставится в
    /// очередь — см. LabAnalyteEnrichmentRequestService.RequestAsync.</summary>
    public static readonly Guid Unresolved = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
