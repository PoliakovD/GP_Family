using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Поля ручного одобрения (ADR-0018), общие для трёх таблиц задач обогащения справочника
/// (<see cref="MedicationEnrichmentJob"/>, <see cref="VisitMedicationEnrichmentJob"/>,
/// <see cref="LabAnalyteEnrichmentJob"/>). Нужны, чтобы очередь «Одобрение» и общая логика
/// гейтов работали с тремя видами задач одним кодом.
/// </summary>
public interface IReviewableEnrichmentJob : IPipelineJob
{
    string NormalizedName { get; }
    string SourceDisplayName { get; }

    /// <summary>Единицы измерения через "; " (только у показателей, у препаратов null).</summary>
    string? Units => null;
    string? Provider { get; set; }
    Guid? KbId { get; set; }

    /// <summary>Уверенность модели-стража (легитимность/правдоподобность) 0..1; null — не вернула
    /// или невалидна (трактуется как «ниже порога»).</summary>
    double? QueryConfidence { get; set; }

    string? QueryConfidenceReason { get; set; }

    /// <summary>Текст запроса, который уйдёт в платный провайдер; админ может его поправить.</summary>
    string? ProposedQueryText { get; set; }

    /// <summary>Не null — платный поиск одобрен админом, повторно не спрашиваем.</summary>
    DateTime? SearchApprovedAt { get; set; }

    /// <summary>JSON-черновик результата суммаризации (ждёт ревью, в kb не записан).</summary>
    string? DraftPayloadJson { get; set; }

    double? ResultConfidence { get; set; }

    string? ResultConfidenceReason { get; set; }

    Guid? ReviewedByUserId { get; set; }

    DateTime? ReviewedAt { get; set; }

    string? ReviewNote { get; set; }
}
