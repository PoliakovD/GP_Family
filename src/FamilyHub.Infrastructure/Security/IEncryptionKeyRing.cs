namespace FamilyHub.Infrastructure.Security;

/// <summary>
/// Связка ключей at-rest шифрования (ADR-0002 §5 / ADR-0009): один активный ключ, которым
/// шифруются НОВЫЕ значения/блобы, и произвольное число отставных — только для расшифровки уже
/// существующих значений/блобов по keyId, зашитому в них (<c>enc:{keyId}:...</c> для полей,
/// <c>FHE1|keyId|...</c> для файлов).
///
/// Ротация: добавить новый ключ как активный (старый переезжает в отставные) → значения читаются
/// сразу, пишутся уже новым → EncryptionRotationJob (Api/Features/Admin) фоново перешифровывает
/// хвост старым→новым → отставной ключ убирается из конфигурации.
/// </summary>
public interface IEncryptionKeyRing
{
    /// <summary>keyId, которым шифруются новые значения/блобы.</summary>
    string ActiveKeyId { get; }

    /// <summary>Материал активного ключа (32 байта).</summary>
    byte[] ActiveKey { get; }

    /// <summary>keyId всех отставных ключей связки (без активного) — для отчёта в админке.</summary>
    IReadOnlyList<string> PreviousKeyIds { get; }

    /// <summary>Ключ по keyId (активный или отставной). Бросает, если keyId связке неизвестен.</summary>
    byte[] ForKeyId(string keyId);

    /// <summary>true, если keyId — активный или один из отставных.</summary>
    bool IsKnownKeyId(string keyId);
}
