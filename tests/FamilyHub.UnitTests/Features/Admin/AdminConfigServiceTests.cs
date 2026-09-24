using System.Text.Json;
using FamilyHub.Api.Configuration;
using FamilyHub.Api.Features.Admin;
using FamilyHub.Infrastructure.Auth.Jwt;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Previews;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyHub.UnitTests.Features.Admin;

/// <summary>
/// «Настройки → Конфиг env» — главное свойство сервиса: ни один секрет не покидает бэкенд.
/// Проверяется на структурном уровне (секрет не имеет поля для значения) и «чёрным ящиком»
/// (сентинел-значения секретов не встречаются нигде в сериализованном ответе).
/// </summary>
public class AdminConfigServiceTests
{
    private const string Sentinel = "sentinel-DO-NOT-LEAK-9f3a";

    private static AdminConfigService CreateService(IConfiguration? configuration = null) => new(
        configuration ?? new ConfigurationBuilder().Build(),
        Options.Create(new EnrichmentOptions { Provider = MedicationSearchProviderKind.Yandex, ApiKey = Sentinel + "-api", FolderId = Sentinel + "-folder", PricePerPaidCall = 1.5m }),
        Options.Create(new LmStudioOptions()),
        Options.Create(new ExtractionOptions()),
        Options.Create(new ExtractionLimitsOptions()),
        Options.Create(new PreviewOptions()),
        Options.Create(new MinioOptions { AccessKey = Sentinel + "-access", SecretKey = Sentinel + "-secret", Endpoint = "minio:9000" }),
        Options.Create(new MessagingOptions()),
        Options.Create(new JwtOptions { SigningKey = Sentinel + "-jwt", PreviousSigningKeys = [new JwtKeyEntry { Id = "v0", Material = Sentinel + "-jwt-old" }] }),
        Options.Create(new AttachmentDownloadOptions { DownloadSigningKey = Sentinel + "-dl", PreviousSigningKeys = [Sentinel + "-dl-old"] }),
        Options.Create(new AdminOptions { Enabled = true, User = Sentinel + "-user", Password = Sentinel + "-pass" }));

    private static IEnumerable<ConfigItemDto> AllItems(AdminConfigDto dto) => dto.Sections.SelectMany(s => s.Items);

    [Fact]
    public void Get_SerializedResponse_ContainsNoSecretValue()
    {
        var json = JsonSerializer.Serialize(CreateService().Get());

        json.Should().NotContain(Sentinel);
    }

    [Fact]
    public void Get_ItemWhoseNameLooksSecret_IsSecretWithoutValue()
    {
        var items = AllItems(CreateService().Get()).ToList();

        var secretLike = items.Where(i => AdminConfigService.SecretLikeName().IsMatch(i.Key.Split(':').Last())).ToList();

        secretLike.Should().NotBeEmpty("ApiKey/AccessKey/SecretKey/Password должны быть в выдаче — как «задан / не задан»");
        secretLike.Should().OnlyContain(i => i.IsSecret && i.Value == null);
    }

    [Fact]
    public void Get_SecretItem_ReportsWhetherItIsSet()
    {
        var items = AllItems(CreateService().Get()).ToDictionary(i => i.Key);

        items["Enrichment:ApiKey"].IsSet.Should().BeTrue();
        items["Minio:SecretKey"].IsSet.Should().BeTrue();
        items["Admin:Password"].IsSet.Should().BeTrue();
    }

    [Fact]
    public void Get_KeyRingSecrets_AreNotListedAtAll()
    {
        // Мастер-ключ, ключи подписи JWT/ссылок — не «задан/не задан», а вообще вне этого вида:
        // их состояние показывает «Безопасность → Ключи и ротация».
        var keys = AllItems(CreateService().Get()).Select(i => i.Key).ToList();

        keys.Should().NotContain(k => k.Contains("SigningKey") || k.Contains("MasterKey") || k.Contains("Material"));
    }

    [Fact]
    public void Get_FormatsValuesInvariantly()
    {
        var items = AllItems(CreateService().Get()).ToDictionary(i => i.Key);

        items["Jwt:AccessTokenLifetime"].Value.Should().Be("00:15:00");
        items["Extraction:Enabled"].Value.Should().Be("true");
        items["Enrichment:Provider"].Value.Should().Be("Yandex");
        items["Enrichment:PricePerPaidCall"].Value.Should().Be("1.5");
        items["Minio:Endpoint"].Value.Should().Be("minio:9000");
    }

    [Fact]
    public void SourceOf_KeyNotConfiguredAnywhere_IsDefault()
    {
        AdminConfigService.SourceOf(new ConfigurationBuilder().Build(), "FhTestAdminCfg:Nothing").Should().Be("default");
    }

    [Fact]
    public void SourceOf_JsonFile_IsAppsettings_AndEnvironmentOverridesIt()
    {
        var file = Path.Combine(Path.GetTempPath(), $"fh-adminconfig-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "FhTestAdminCfg": { "Probe": "from-json", "OnlyJson": "x" } }""");
        Environment.SetEnvironmentVariable("FhTestAdminCfg__Probe", "from-env");
        try
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(file).AddEnvironmentVariables().Build();

            AdminConfigService.SourceOf(configuration, "FhTestAdminCfg:OnlyJson").Should().Be("appsettings");
            AdminConfigService.SourceOf(configuration, "FhTestAdminCfg:Probe").Should().Be("env");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FhTestAdminCfg__Probe", null);
            File.Delete(file);
        }
    }
}
