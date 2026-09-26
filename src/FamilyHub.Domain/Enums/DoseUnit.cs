namespace FamilyHub.Domain.Enums;

/// <summary>Единица дозы. Из неё строится подпись («1 таб.») и разбирается остаток в аптечке.</summary>
public enum DoseUnit
{
    Tablet = 0,
    Capsule = 1,
    Ml = 2,
    Drop = 3,
    Sachet = 4,
    Dose = 5,
}
