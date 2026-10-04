using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// «Одобрение» просило платный поиск показателя, статья которого уже есть в справочнике: строка заведена до
/// кросс-алфавитной свёртки ключа («treponema pallidum»), а новое распознавание даёт свёрнутый ключ — поиск по
/// справочнику её не находил. «Перепроверить по справочнику» (LabAnalyteKbRekeyJob + LabAnalyteParkedKbResolver)
/// перекейивает строку и закрывает такие поиски. Биоматериал — случайный Guid на тест (SpecimenKbId не FK), чтобы
/// строки справочника и задачи не пересекались с другими тестами коллекции.
/// </summary>
[Collection(AdminIntegrationCollection.Name)]
public class KbStaleKeyRecheckTests(AdminWebFactory factory)
{
    private const string FullBlankName =
        "Treponema pallidum (Антитела IgG и IgM к трепонеме паллидум, обнаружение в сыворотке крови)";

    private record RecheckDto(int Resolved);
    private record KbMatchDto(Guid KbId, string DisplayName, string Reason, double Score, string? Units);
    private record ItemDto(Guid Id, string Stage, KbMatchDto? KbMatch);

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }))
            .EnsureSuccessStatusCode();
        return client;
    }

    private async Task<Guid> SeedKbAsync(string normalizedName, Guid specimenKbId, string[] aliases)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        const string EmptyPayload = "{}";
        // Проверенная админом строка — именно такие переживали пересборку справочника со старым ключом.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO kb.global_lab_analytes_kb
                ("Id", "NormalizedName", "SpecimenKbId", "DisplayName", "PayloadJson", "PayloadVersion", "Source", "Aliases",
                 "LockedFields", "VerificationStatus", "CreatedAt", "UpdatedAt")
            VALUES
                ({id}, {normalizedName}, {specimenKbId}, {FullBlankName}, {EmptyPayload}::jsonb, 1, 'test', {aliases},
                 {new[] { "displayName" }}, {(int)KbVerificationStatus.AdminVerified}, {now}, {now})
            """);
        return id;
    }

    private async Task<Guid> SeedParkedJobAsync(Guid specimenKbId, bool force = false, string? units = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = new LabAnalyteEnrichmentJob
        {
            Id = Guid.NewGuid(),
            NormalizedName = LabAnalyteNormalizer.NormalizeAnalyteKey(FullBlankName),
            SpecimenKbId = specimenKbId,
            SourceDisplayName = FullBlankName,
            RequestedByUserId = Guid.Empty,
            Origin = EnrichmentRequestOrigin.Extraction,
            Force = force,
            Units = units,
            Status = EnrichmentJobStatus.AwaitingSearchApproval,
            ProposedQueryText = FullBlankName,
            CreatedAt = DateTime.UtcNow,
        };
        db.LabAnalyteEnrichmentJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<(string NormalizedName, string[] Aliases)> ReadKbAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var name = await db.GlobalLabAnalytesKb.AsNoTracking().Where(k => k.Id == id).Select(k => k.NormalizedName).SingleAsync();
        var aliases = await db.Database.SqlQuery<string[]>(
            $"""SELECT "Aliases" AS "Value" FROM kb.global_lab_analytes_kb WHERE "Id" = {id}""").SingleAsync();
        return (name, aliases);
    }

    private async Task<LabAnalyteEnrichmentJob> ReadJobAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.LabAnalyteEnrichmentJobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    [Fact]
    public async Task Recheck_StaleLatinKbKey_RekeysRow_AndClosesParkedSearch()
    {
        var client = await AuthenticatedClientAsync();
        var specimen = Guid.NewGuid();
        var kbId = await SeedKbAsync("treponema pallidum", specimen, ["treponema pallidum igm", "сифилис tpha"]);
        var jobId = await SeedParkedJobAsync(specimen);

        var response = await client.PostAsJsonAsync("/api/admin/review/searches/recheck-kb", new { });
        response.EnsureSuccessStatusCode();
        (await response.Content.ReadFromJsonAsync<RecheckDto>())!.Resolved.Should().BeGreaterThanOrEqualTo(1);

        var job = await ReadJobAsync(jobId);
        job.Status.Should().Be(EnrichmentJobStatus.Completed);
        job.KbId.Should().Be(kbId);

        var (name, aliases) = await ReadKbAsync(kbId);
        name.Should().Be(LabAnalyteNormalizer.NormalizeAnalyteKey(FullBlankName));
        aliases.Should().Contain(LabAnalyteNormalizer.RenormalizeKey("treponema pallidum igm"))
            .And.Contain(LabAnalyteNormalizer.RenormalizeKey("сифилис tpha"))
            .And.NotContain("treponema pallidum igm");
    }

    [Fact]
    public async Task Recheck_RenormalizedKeyTakenByAnotherRow_KeepsKey_AndAddsItAsAlias()
    {
        var client = await AuthenticatedClientAsync();
        var specimen = Guid.NewGuid();
        var foldedKey = LabAnalyteNormalizer.RenormalizeKey("treponema pallidum");
        var staleId = await SeedKbAsync("treponema pallidum", specimen, []);
        var currentId = await SeedKbAsync(foldedKey, specimen, []);

        (await client.PostAsJsonAsync("/api/admin/review/searches/recheck-kb", new { })).EnsureSuccessStatusCode();

        var (staleName, staleAliases) = await ReadKbAsync(staleId);
        staleName.Should().Be("treponema pallidum", "новый ключ занят — объединение дублей остаётся ручным");
        staleAliases.Should().Contain(foldedKey);
        (await ReadKbAsync(currentId)).NormalizedName.Should().Be(foldedKey);
    }

    [Fact]
    public async Task Recheck_ForceUnitGapJob_StaysParked_AndDetailExplainsKbMatch()
    {
        var client = await AuthenticatedClientAsync();
        var specimen = Guid.NewGuid();
        var kbId = await SeedKbAsync(LabAnalyteNormalizer.NormalizeAnalyteKey(FullBlankName), specimen, []);
        var jobId = await SeedParkedJobAsync(specimen, force: true, units: "КП");

        (await client.PostAsJsonAsync("/api/admin/review/searches/recheck-kb", new { })).EnsureSuccessStatusCode();

        (await ReadJobAsync(jobId)).Status.Should().Be(EnrichmentJobStatus.AwaitingSearchApproval,
            "force-задача идёт за нормой в единицах бланка — попадание в справочник для неё ожидаемо");

        var item = await client.GetFromJsonAsync<ItemDto>($"/api/admin/review/items/lab-analyte/{jobId}");
        item!.KbMatch.Should().NotBeNull();
        item.KbMatch!.KbId.Should().Be(kbId);
        item.KbMatch.Reason.Should().Be("unit-gap");
        item.KbMatch.Units.Should().Be("КП");
    }
}
