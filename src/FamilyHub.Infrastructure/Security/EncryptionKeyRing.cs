namespace FamilyHub.Infrastructure.Security;

/// <inheritdoc cref="IEncryptionKeyRing"/>
public class EncryptionKeyRing : IEncryptionKeyRing
{
    private readonly Dictionary<string, byte[]> _keysByKeyId;

    public string ActiveKeyId { get; }
    public byte[] ActiveKey { get; }
    public IReadOnlyList<string> PreviousKeyIds { get; }

    /// <summary>
    /// Собирает и валидирует связку сразу при конструировании (не лениво на первое обращение) —
    /// нужно вызвать это до <c>builder.Build()</c>, чтобы битая конфигурация (дубли keyId,
    /// некорректный base64) валила старт хоста, а не первый запрос, коснувшийся [Encrypted]-поля.
    /// </summary>
    public EncryptionKeyRing(EncryptionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ActiveKeyId))
            throw new InvalidOperationException(
                "Encryption:ActiveKeyId не задан — идентификатор активного ключа обязателен.");

        var keys = new Dictionary<string, byte[]>
        {
            [options.ActiveKeyId] = DecodeKey(options.MasterKey, "Encryption:MasterKey"),
        };

        var previousIds = new List<string>(options.PreviousKeys.Count);
        foreach (var entry in options.PreviousKeys)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
                throw new InvalidOperationException(
                    "Encryption:PreviousKeys содержит запись без Id (env Encryption__PreviousKeys__N__Id).");
            if (!keys.TryAdd(entry.Id, DecodeKey(entry.Material, $"Encryption:PreviousKeys[{entry.Id}].Material")))
                throw new InvalidOperationException(
                    $"Encryption:PreviousKeys содержит дублирующийся keyId «{entry.Id}» — совпадает с " +
                    "ActiveKeyId или другой отставной записью. Каждый keyId в связке обязан быть уникален.");
            previousIds.Add(entry.Id);
        }

        _keysByKeyId = keys;
        ActiveKeyId = options.ActiveKeyId;
        ActiveKey = keys[options.ActiveKeyId];
        PreviousKeyIds = previousIds;
    }

    public byte[] ForKeyId(string keyId)
    {
        if (_keysByKeyId.TryGetValue(keyId, out var key))
            return key;

        var known = PreviousKeyIds.Count == 0
            ? "нет"
            : string.Join(", ", PreviousKeyIds);
        throw new InvalidOperationException(
            $"Значение зашифровано ключом «{keyId}», которого нет в связке (активен «{ActiveKeyId}», " +
            $"отставные: {known}) — требуется добавить Encryption__PreviousKeys__N__Id={keyId} и " +
            "Encryption__PreviousKeys__N__Material с материалом этого ключа.");
    }

    public bool IsKnownKeyId(string keyId) => _keysByKeyId.ContainsKey(keyId);

    /// <summary>Валидация формата ключа — общая для активного и любого отставного.</summary>
    internal static byte[] DecodeKey(string masterKeyBase64, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(masterKeyBase64))
            throw new InvalidOperationException($"{sourceName} не задан — at-rest шифрование невозможно.");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(masterKeyBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{sourceName} не является корректным base64.", ex);
        }

        if (key.Length != 32)
            throw new InvalidOperationException(
                $"{sourceName} должен быть 32 байта (AES-256), получено {key.Length}.");
        return key;
    }
}
