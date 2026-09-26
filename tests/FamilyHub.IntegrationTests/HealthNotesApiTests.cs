using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.HealthNotes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Личный дневник самочувствия: изоляция по владельцу, валидация, право на забвение и экспорт.</summary>
public class HealthNotesApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private static object SymptomBody(string title = "Головная боль") => new
    {
        kind = (int)HealthNoteKind.Symptom,
        occurredAt = DateTime.UtcNow.AddMinutes(-10),
        title,
        text = "после работы",
        includeInDoctorQuestions = false,
        symptom = new { severity = 7, areas = new[] { "head" }, detail = "затылок" },
    };

    [Fact]
    public async Task Create_ThenList_ReturnsOwnNote_AndOtherUserSeesNothing()
    {
        var owner = ClientAs(FreshTelegramId());
        var stranger = ClientAs(FreshTelegramId());

        var created = await owner.PostAsJsonAsync("/api/health-notes", SymptomBody());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await created.Content.ReadFromJsonAsync<HealthNoteDto>(JsonOpts);
        dto!.Symptom!.Severity.Should().Be(7);

        var mine = await owner.GetFromJsonAsync<List<HealthNoteDto>>("/api/health-notes", JsonOpts);
        mine.Should().ContainSingle(n => n.Id == dto.Id);

        var theirs = await stranger.GetFromJsonAsync<List<HealthNoteDto>>("/api/health-notes", JsonOpts);
        theirs.Should().BeEmpty();

        (await stranger.DeleteAsync($"/api/health-notes/{dto.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.PutAsJsonAsync($"/api/health-notes/{dto.Id}", SymptomBody("Взлом")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_InvalidSeverity_Returns400()
    {
        var owner = ClientAs(FreshTelegramId());
        var body = new
        {
            kind = (int)HealthNoteKind.Symptom,
            occurredAt = DateTime.UtcNow.AddMinutes(-1),
            title = "Боль",
            symptom = new { severity = 99 },
        };

        (await owner.PostAsJsonAsync("/api/health-notes", body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Catalog_ListsMetricsAndReferenceKeys()
    {
        var owner = ClientAs(FreshTelegramId());

        var catalog = await owner.GetFromJsonAsync<JsonElement>("/api/health-notes/catalog", JsonOpts);

        catalog.GetProperty("metrics").EnumerateArray().Select(m => m.GetProperty("code").GetString())
            .Should().Contain(["blood_pressure", "pulse", "weight"]);
        catalog.GetProperty("bodyAreas").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Metric_Series_ReturnsPointsForRequestedCode()
    {
        var owner = ClientAs(FreshTelegramId());
        (await owner.PostAsJsonAsync("/api/health-notes", new
        {
            kind = (int)HealthNoteKind.Metric,
            occurredAt = DateTime.UtcNow.AddHours(-2),
            metric = new { code = "blood_pressure", value = 128, value2 = 84 },
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        var series = await owner.GetFromJsonAsync<JsonElement>("/api/health-notes/metrics/blood_pressure/series", JsonOpts);

        series.GetArrayLength().Should().Be(1);
        series[0].GetProperty("value").GetDecimal().Should().Be(128m);
        (await owner.GetAsync("/api/health-notes/metrics/nonsense/series")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteAccount_ErasesHealthNotes()
    {
        var telegramId = FreshTelegramId();
        var owner = ClientAs(telegramId);
        var dto = await (await owner.PostAsJsonAsync("/api/health-notes", SymptomBody()))
            .Content.ReadFromJsonAsync<HealthNoteDto>(JsonOpts);

        (await owner.PostAsJsonAsync("/api/account/delete", new { confirm = "DELETE" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.HealthNotes.AnyAsync(n => n.Id == dto!.Id)).Should().BeFalse("дневник удаляется вместе с аккаунтом");
    }

    [Fact]
    public async Task Export_ContainsDecryptedHealthNotes()
    {
        var owner = ClientAs(FreshTelegramId());
        await owner.PostAsJsonAsync("/api/health-notes", SymptomBody("Экспортная мигрень"));

        var response = await owner.GetAsync("/api/account/export");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("health-notes.json")!.Open());
        (await reader.ReadToEndAsync()).Should().Contain("Экспортная мигрень", "экспорт содержит расшифрованные поля");
    }
}
