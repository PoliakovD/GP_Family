namespace FamilyHub.Domain.Enums;

/// <summary>
/// Вид персональной мед-записи (см. MedicalRecord). Не шифруется — по нему фильтруются списки и
/// поиск прямо в SQL, до расшифровки остальных полей. Analysis = 0 намеренно: существующие до
/// введения этого поля записи получают этот дефолт при миграции и остаются анализами.
/// </summary>
public enum MedicalRecordKind
{
    Analysis = 0,
    DoctorVisit = 1,
}
