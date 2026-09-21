using FamilyHub.Api.Configuration;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Storage;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — at-rest шифрование (этап 2, 152-ФЗ; ротация
/// ключей — ADR-0009), без изменения поведения/порядка.
/// </summary>
public static class EncryptionRegistration
{
    /// <param name="devTools">Нужен только для guard'а на утёкший dev-ключ (условие завязано на
    /// DevTools:DevAuthEnabled, не на ASPNETCORE_ENVIRONMENT — см. комментарий ниже).</param>
    public static WebApplicationBuilder AddFamilyHubEncryption(this WebApplicationBuilder builder, DevToolsOptions devTools)
    {
        // --- At-rest шифрование (этап 2, 152-ФЗ; ротация ключей — ADR-0009): связка ключей вне БД, ---
        // --- fail-fast при отсутствии/битой конфигурации. Синглтоны обязательны: EF кэширует модель ---
        // --- с конвертером, захватившим первый cipher. ---
        var encryptionOptions = builder.Configuration.GetSection(EncryptionOptions.SectionName).Get<EncryptionOptions>()
            ?? new EncryptionOptions();
        if (string.IsNullOrWhiteSpace(encryptionOptions.MasterKey))
            throw new InvalidOperationException(
                "Encryption:MasterKey не задан (env Encryption__MasterKey) — at-rest шифрование обязательно.");
        // appsettings.Development.json и docker-compose.yml больше НЕ содержат дефолт этого ключа —
        // секреты везде тянутся из окружения, даже в Development (см. .env.example). Единственное
        // оставшееся легитимное место с этим значением — DesignTimeDbContextFactory.DevMasterKey
        // (design-time `dotnet ef`/тестовые фабрики, реальных данных не касается). Но строка всё
        // равно навсегда осталась в истории git — этот guard блокирует её случайное копирование в
        // реальное окружение. Условие теперь завязано на DevTools:DevAuthEnabled, а не на
        // ASPNETCORE_ENVIRONMENT — контур на VPS дев по защите, но Production по среде (см. выше).
        // Проверяется и активный ключ, и каждый отставной (Encryption:PreviousKeys, ADR-0009) — иначе
        // утёкший dev-ключ можно было бы протащить в связку как "отставной" в обход guard'а.
        var leakedDevKeyIds = new List<string>();
        if (encryptionOptions.MasterKey == DesignTimeDbContextFactory.DevMasterKey)
            leakedDevKeyIds.Add(encryptionOptions.ActiveKeyId);
        leakedDevKeyIds.AddRange(encryptionOptions.PreviousKeys
            .Where(k => k.Material == DesignTimeDbContextFactory.DevMasterKey)
            .Select(k => k.Id));
        if (!devTools.DevAuthEnabled && leakedDevKeyIds.Count > 0)
            throw new InvalidOperationException(
                $"Ключ(и) шифрования с keyId {string.Join(", ", leakedDevKeyIds)} равны design-time/тестовому " +
                "dev-ключу из истории репозитория — при выключенном DevTools:DevAuthEnabled это недопустимо. " +
                "Сгенерировать реальный ключ: `openssl rand -base64 32`.");
        // Связка строится здесь (не лениво в DI) — битая конфигурация (дубли keyId, некорректный
        // base64/длина ключа) валит старт хоста сразу, а не первый запрос, коснувшийся [Encrypted]-поля.
        var encryptionKeyRing = new EncryptionKeyRing(encryptionOptions);
        builder.Services.AddSingleton<IEncryptionKeyRing>(encryptionKeyRing);
        builder.Services.AddSingleton<IFieldCipher, AesGcmFieldCipher>();
        builder.Services.AddSingleton<IFileCipher, AesGcmFileCipher>();
        builder.Services.AddSingleton<DownloadTokenService>();

        // Fail-fast для ключа подписи ссылок на скачивание вложений — без него DownloadTokenService.Sign
        // бросал бы лениво, только при первой попытке выдать ссылку (см. находку 09.2 аудита безопасности).
        if (string.IsNullOrWhiteSpace(builder.Configuration["Attachments:DownloadSigningKey"]))
            throw new InvalidOperationException(
                "Attachments:DownloadSigningKey не задан (env Attachments__DownloadSigningKey) — " +
                "выдача ссылок на вложения невозможна.");

        return builder;
    }
}
