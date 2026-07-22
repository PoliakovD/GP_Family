namespace FamilyHub.Infrastructure.Security;

/// <summary>Настройки at-rest шифрования (этап 2, 152-ФЗ). Ключ — только из окружения, вне БД.</summary>
public class EncryptionOptions
{
    public const string SectionName = "Encryption";

    /// <summary>Мастер-ключ AES-256 в base64 (32 байта). Обязателен вне Development.</summary>
    public string MasterKey { get; set; } = string.Empty;

    /// <summary>Идентификатор активного ключа — пишется в префикс шифротекста (задел под ротацию).</summary>
    public string ActiveKeyId { get; set; } = "v1";

    /// <summary>
    /// Читать значения без префикса "enc:" как открытый текст (данные, созданные до этапа 2).
    /// Запись всегда шифруется. Выключить после полной перешифровки данных.
    /// </summary>
    public bool AllowLegacyPlaintextRead { get; set; } = true;
}
