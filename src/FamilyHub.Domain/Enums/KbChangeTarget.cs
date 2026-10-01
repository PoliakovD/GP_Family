namespace FamilyHub.Domain.Enums;

/// <summary>Что изменила запись журнала <see cref="Entities.KbChangeLog"/> (ADR-0018).</summary>
public enum KbChangeTarget
{
    LabAnalyteKb = 0,
    MedicationKb = 1,
    LabAnalyteSearchCache = 2,
    MedicationSearchCache = 3,
}
