using System.Net;
using System.Net.Http.Json;
using FamilyHub.Infrastructure.Auth.Jwt;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// «Настройки → Конфиг env» (/api/admin/config) через реальный хост. Главное, что здесь доказывается
/// — секреты не покидают бэкенд: сентинел Enrichment:ApiKey и РЕАЛЬНЫЕ секреты, с которыми поднят
/// хост (ключ MinIO, ключ подписи JWT, мастер-ключ шифрования, пароль админа), не встречаются в теле
/// ответа ни в каком виде.
/// </summary>
[Collection(AdminIntegrationCollection.Name)]
public class AdminConfigApiTests(AdminWebFactory factory)
{
    private record Item(string Key, string? Value, bool IsSecret, bool IsSet, string Source);
    private record Section(string Name, string Title, List<Item> Items);
    private record Config(List<Section> Sections);

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }))
            .EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Config_WithoutSession_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("/api/admin/config");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Config_WithSession_ReturnsSectionsWithSources()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync("/api/admin/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var config = (await response.Content.ReadFromJsonAsync<Config>())!;
        config.Sections.Select(s => s.Name).Should().Contain(["Enrichment", "LmStudio", "Minio"]);
        config.Sections.SelectMany(s => s.Items).Should().OnlyContain(i =>
            new[] { "env", "appsettings", "cli", "other", "default" }.Contains(i.Source));
    }

    [Fact]
    public async Task Config_EnrichmentApiKey_IsReportedAsSetWithoutItsValue()
    {
        var client = await AuthenticatedClientAsync();

        var config = (await client.GetFromJsonAsync<Config>("/api/admin/config"))!;

        var apiKey = config.Sections.Single(s => s.Name == "Enrichment").Items.Single(i => i.Key == "Enrichment:ApiKey");
        apiKey.IsSecret.Should().BeTrue();
        apiKey.IsSet.Should().BeTrue();
        apiKey.Value.Should().BeNull();
    }

    [Fact]
    public async Task Config_ResponseBody_ContainsNoSecretAnywhere()
    {
        var client = await AuthenticatedClientAsync();
        var sp = factory.Services;

        var body = await client.GetStringAsync("/api/admin/config");

        var secrets = new[]
        {
            AdminWebFactory.EnrichmentApiKeySentinel,
            AdminWebFactory.TestPassword,
            AdminWebFactory.PreviousDownloadKey,
            sp.GetRequiredService<IOptions<MinioOptions>>().Value.SecretKey,
            sp.GetRequiredService<IOptions<MinioOptions>>().Value.AccessKey,
            sp.GetRequiredService<IOptions<JwtOptions>>().Value.SigningKey,
            sp.GetRequiredService<IOptions<EncryptionOptions>>().Value.MasterKey,
            sp.GetRequiredService<IOptions<AttachmentDownloadOptions>>().Value.DownloadSigningKey,
        };

        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)))
            body.Should().NotContain(secret);
    }
}
