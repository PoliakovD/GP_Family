using System.Net;
using System.Net.Http.Json;
using System.Text;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// UX «ждём ИИ»: пока LM Studio недоступен (в тестовом хосте он направлен на закрытый порт, см.
/// FamilyHubWebFactory), пользовательские LLM-действия не пропадают и не падают ошибкой, а встают в
/// очередь и видны как ожидающие.
/// </summary>
public class AiUnavailableApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record AiStatusDto(bool Available);

    [Fact]
    public async Task AiStatus_WithoutSession_Returns401()
    {
        var response = await Factory.CreateClient().GetAsync("/api/ai/status");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AiStatus_WhenLmStudioDown_ReportsUnavailable()
    {
        var user = ClientAs(FreshTelegramId());

        var response = await user.GetAsync("/api/ai/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<AiStatusDto>(JsonOpts))!.Available.Should().BeFalse();
    }

    [Fact]
    public async Task RequestExtraction_WhenAiDown_IsQueuedAsWaiting_NotRefused()
    {
        var owner = ClientAs(FreshTelegramId());
        var created = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(new DateOnly(2026, 1, 1), null, null, null));
        var recordId = (await created.Content.ReadFromJsonAsync<MedicalRecordDto>())!.Id;

        var upload = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("scan"));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        upload.Add(file, "file", "scan.pdf");
        (await owner.PostAsync($"/api/medical-records/{recordId}/attachments", upload)).StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await owner.PostAsync($"/api/medical-records/{recordId}/extract", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var status = await (await owner.GetAsync($"/api/medical-records/{recordId}/extraction"))
            .Content.ReadFromJsonAsync<ExtractionStatusResponse>(JsonOpts);
        status!.WaitingForAi.Should().BeTrue("ИИ недоступен — задача ждёт его, а не потеряна и не упала");
        status.QueuePosition.Should().Be(0, "позиция в очереди к модели при ожидании ИИ не показывается");

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.MedicalDocumentExtractionJobs.AsNoTracking().SingleAsync(j => j.MedicalRecordId == recordId))
            .WaitingForAi.Should().BeTrue();

        // Повторное нажатие «Распознать» не плодит вторую задачу — уже есть живая (ожидающая).
        var again = await owner.PostAsync($"/api/medical-records/{recordId}/extract", null);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PendingSpecimen_IsStoredForOwnerOnly_AndExposedOnRecord()
    {
        var owner = ClientAs(FreshTelegramId());
        var created = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(new DateOnly(2026, 1, 1), null, null, null));
        var recordId = (await created.Content.ReadFromJsonAsync<MedicalRecordDto>())!.Id;

        (await owner.PutAsJsonAsync($"/api/medical-records/{recordId}/specimen-pending", new SetPendingSpecimenRequest("Слюна")))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PutAsJsonAsync($"/api/medical-records/{recordId}/specimen-pending", new SetPendingSpecimenRequest("x")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var stranger = ClientAs(FreshTelegramId());
        (await stranger.PutAsJsonAsync($"/api/medical-records/{recordId}/specimen-pending", new SetPendingSpecimenRequest("Кровь")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var record = await (await owner.GetAsync($"/api/medical-records/{recordId}")).Content.ReadFromJsonAsync<MedicalRecordDto>(JsonOpts);
        record!.PendingSpecimenText.Should().Be("Слюна");
    }
}
