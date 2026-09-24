using System.Globalization;
using System.Text.RegularExpressions;
using FamilyHub.Api.Configuration;
using FamilyHub.Infrastructure.Auth.Jwt;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Previews;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.Extraction;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Read-only вид эффективной конфигурации для «Настройки → Конфиг env»: что именно сейчас действует
/// (Enrichment/LmStudio/Minio/…) и откуда взято — env, appsettings или значение по умолчанию из
/// кода. Раньше эти настройки были видны только тому, кто открывал .env на сервере.
///
/// Безопасность — главное свойство класса, три слоя:
///  1. ЯВНЫЙ белый список: значения берутся из типизированных <c>IOptions&lt;T&gt;</c> и описаны
///     поимённо ниже, а не перечислением <c>IConfiguration</c> — новая настройка (в том числе новый
///     секрет) сама в выдачу не попадёт.
///  2. Секрет описывается через <see cref="SectionBuilder.Secret"/>, который принимает только bool
///     «задан ли» — самого значения в DTO нет физически.
///  3. <see cref="SectionBuilder.Value"/> отказывается принимать свойство, чьё имя похоже на секрет
///     (Key/Secret/Password) — забыть пометить секрет как секрет нельзя, упадёт тест/первый запрос.
/// Ключи подписи и мастер-ключ сюда не входят вовсе: их состояние (id, число отставных) уже
/// показывает страница «Безопасность → Ключи и ротация».
/// </summary>
public partial class AdminConfigService(
    IConfiguration configuration,
    IOptions<EnrichmentOptions> enrichment,
    IOptions<LmStudioOptions> lmStudio,
    IOptions<ExtractionOptions> extraction,
    IOptions<ExtractionLimitsOptions> extractionLimits,
    IOptions<PreviewOptions> previews,
    IOptions<MinioOptions> minio,
    IOptions<MessagingOptions> messaging,
    IOptions<JwtOptions> jwt,
    IOptions<AttachmentDownloadOptions> attachments,
    IOptions<AdminOptions> admin)
{
    /// <summary>Имя свойства, похожее на секрет — такое нельзя выводить как обычное значение.
    /// «Token» намеренно не входит: <c>AccessTokenLifetime</c> — обычный срок, не секрет.</summary>
    [GeneratedRegex("key|secret|password", RegexOptions.IgnoreCase)]
    public static partial Regex SecretLikeName();

    public AdminConfigDto Get()
    {
        var e = enrichment.Value;
        var l = lmStudio.Value;
        var x = extraction.Value;
        var xl = extractionLimits.Value;
        var p = previews.Value;
        var m = minio.Value;
        var k = messaging.Value;
        var j = jwt.Value;
        var a = attachments.Value;
        var ad = admin.Value;

        var sections = new List<ConfigSectionDto>
        {
            new SectionBuilder(configuration, EnrichmentOptions.SectionName, "Обогащение справочника (веб-поиск)")
                .Value("Provider", e.Provider)
                .Secret("ApiKey", !string.IsNullOrEmpty(e.ApiKey))
                .Secret("FolderId", !string.IsNullOrEmpty(e.FolderId))
                .Value("MinRefreshIntervalMonths", e.MinRefreshIntervalMonths)
                .Value("MaxSnippets", e.MaxSnippets)
                .Value("TimeoutSeconds", e.TimeoutSeconds)
                .Value("PricePerPaidCall", e.PricePerPaidCall)
                .Build(),

            // Model/Reasoning здесь — значение из env (запасное); действующее, выбранное в админке,
            // показывает «Настройки → ИИ-модель».
            new SectionBuilder(configuration, LmStudioOptions.SectionName, "LM Studio")
                .Value("BaseUrl", l.BaseUrl)
                .Value("Model", l.Model)
                .Value("Reasoning", l.Reasoning)
                .Value("TimeoutSeconds", l.TimeoutSeconds)
                .Build(),

            new SectionBuilder(configuration, ExtractionOptions.SectionName, "Распознавание документов")
                .Value("Enabled", x.Enabled)
                .Value("MaxPages", x.MaxPages)
                .Value("MaxImageDimension", x.MaxImageDimension)
                .Value("JpegQuality", x.JpegQuality)
                .Value("MaxCharsPerChunk", x.MaxCharsPerChunk)
                .Value("RasterDpi", x.RasterDpi)
                .Build(),

            new SectionBuilder(configuration, ExtractionLimitsOptions.SectionName, "Лимиты распознавания и rate limit")
                .Value("MaxBatchDocuments", xl.MaxBatchDocuments)
                .Value("MaxActiveJobsPerUser", xl.MaxActiveJobsPerUser)
                .Value("DailyJobsPerUser", xl.DailyJobsPerUser)
                .Value("LlmPermitLimit", xl.LlmPermitLimit)
                .Value("LlmWindowSeconds", xl.LlmWindowSeconds)
                .Value("MedicalWritePermitLimit", xl.MedicalWritePermitLimit)
                .Value("MedicalWriteWindowSeconds", xl.MedicalWriteWindowSeconds)
                .Build(),

            new SectionBuilder(configuration, PreviewOptions.SectionName, "Превью вложений")
                .Value("Enabled", p.Enabled)
                .Value("ThumbnailMaxDimension", p.ThumbnailMaxDimension)
                .Value("ThumbnailDpi", p.ThumbnailDpi)
                .Value("PageMaxDimension", p.PageMaxDimension)
                .Value("JpegQuality", p.JpegQuality)
                .Value("GotenbergBaseUrl", p.GotenbergBaseUrl)
                .Value("GotenbergTimeoutSeconds", p.GotenbergTimeoutSeconds)
                .Build(),

            new SectionBuilder(configuration, MinioOptions.SectionName, "Файловое хранилище (MinIO)")
                .Value("Endpoint", m.Endpoint)
                .Value("Bucket", m.Bucket)
                .Value("UseSsl", m.UseSsl)
                .Value("PublicEndpoint", m.PublicEndpoint)
                .Secret("AccessKey", !string.IsNullOrEmpty(m.AccessKey))
                .Secret("SecretKey", !string.IsNullOrEmpty(m.SecretKey))
                .Build(),

            new SectionBuilder(configuration, MessagingOptions.SectionName, "Шина событий (Kafka)")
                .Value("Kafka:Enabled", k.Kafka.Enabled)
                .Value("Kafka:BootstrapServers", k.Kafka.BootstrapServers)
                .Value("Outbox:QueryMessageLimit", k.Outbox.QueryMessageLimit)
                .Value("Retry:RetryLimit", k.Retry.RetryLimit)
                .Build(),

            new SectionBuilder(configuration, JwtOptions.SectionName, "Сессии (JWT)")
                .Value("Issuer", j.Issuer)
                .Value("Audience", j.Audience)
                .Value("AccessTokenLifetime", j.AccessTokenLifetime)
                .Value("RefreshTokenLifetime", j.RefreshTokenLifetime)
                .Value("ClockSkew", j.ClockSkew)
                .Build(),

            new SectionBuilder(configuration, AttachmentDownloadOptions.SectionName, "Ссылки на вложения")
                .Value("UrlTtl", a.UrlTtl)
                .Build(),

            new SectionBuilder(configuration, AdminOptions.SectionName, "Админ-панель")
                .Value("Enabled", ad.Enabled)
                .Secret("User", !string.IsNullOrEmpty(ad.User))
                .Secret("Password", !string.IsNullOrEmpty(ad.Password))
                .Value("SessionLifetime", ad.SessionLifetime)
                .Build(),
        };

        return new AdminConfigDto(sections);
    }

    /// <summary>Где значение ключа реально задано. Провайдеры конфигурации перебираются с конца —
    /// как при чтении: побеждает последний зарегистрированный (env перекрывает appsettings.json).</summary>
    public static string SourceOf(IConfiguration configuration, string key)
    {
        if (configuration is not IConfigurationRoot root) return "other";

        foreach (var provider in root.Providers.Reverse())
        {
            if (!provider.TryGet(key, out _)) continue;

            return provider switch
            {
                EnvironmentVariablesConfigurationProvider => "env",
                JsonConfigurationProvider => "appsettings",
                CommandLineConfigurationProvider => "cli",
                _ => "other",
            };
        }

        return "default";
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private sealed class SectionBuilder(IConfiguration configuration, string sectionName, string title)
    {
        private readonly List<ConfigItemDto> _items = [];

        public SectionBuilder Value(string property, object? value)
        {
            if (SecretLikeName().IsMatch(property))
                throw new InvalidOperationException(
                    $"'{sectionName}:{property}' похоже на секрет — описывать его надо через Secret(), а не Value().");

            var key = $"{sectionName}:{property}";
            _items.Add(new ConfigItemDto(key, Format(value), IsSecret: false, IsSet: value is not null, SourceOf(configuration, key)));
            return this;
        }

        /// <summary>Принимает ТОЛЬКО признак «задан» — значение секрета сюда не передаётся.</summary>
        public SectionBuilder Secret(string property, bool isSet)
        {
            var key = $"{sectionName}:{property}";
            _items.Add(new ConfigItemDto(key, Value: null, IsSecret: true, IsSet: isSet, SourceOf(configuration, key)));
            return this;
        }

        public ConfigSectionDto Build() => new(sectionName, title, _items);
    }
}
