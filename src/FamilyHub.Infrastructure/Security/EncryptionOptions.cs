namespace FamilyHub.Infrastructure.Security;

/// <summary>Настройки at-rest шифрования (этап 2, 152-ФЗ; ротация — ADR-0009). Ключи — только из
/// окружения, вне БД.</summary>
public class EncryptionOptions
{
    public const string SectionName = "Encryption";

    /// <summary>Мастер-ключ AES-256 в base64 (32 байта). Обязателен вне Development.</summary>
    public string MasterKey { get; set; } = string.Empty;

    /// <summary>Идентификатор активного ключа — пишется в префикс шифротекста/блоба, этим ключом
    /// шифруются НОВЫЕ значения.</summary>
    public string ActiveKeyId { get; set; } = "v1";

    /// <summary>
    /// Отставные ключи — только для расшифровки уже существующих значений/блобов по keyId,
    /// зашитому в них (ротация, ADR-0009: старый ключ переезжает сюда, пока
    /// EncryptionRotationJob не перешифрует всё активным). Никогда не используются на запись.
    /// Env: <c>Encryption__PreviousKeys__0__Id</c> / <c>Encryption__PreviousKeys__0__Material</c>.
    /// </summary>
    public List<EncryptionKeyEntry> PreviousKeys { get; set; } = [];
}

/// <summary>Один отставной ключ связки — см. <see cref="EncryptionOptions.PreviousKeys"/>.</summary>
public class EncryptionKeyEntry
{
    /// <summary>keyId, под которым ключ встречается в существующих значениях/блобах.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Материал ключа AES-256 в base64 (32 байта) — тот же формат, что MasterKey.</summary>
    public string Material { get; set; } = string.Empty;
}
