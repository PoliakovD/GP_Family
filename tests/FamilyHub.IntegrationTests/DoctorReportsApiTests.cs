using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FamilyHub.Api.Features.Dependents;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.Modules.Medical.DoctorReports;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Отчёт для врача: создание, публичная ссылка без аккаунта, отзыв, изоляция, забвение.</summary>
public class DoctorReportsApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private static object CreateBody(int? shareDays = 14, string? comment = "Слабость и головные боли") => new
    {
        periodFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-6).ToString("yyyy-MM-dd"),
        periodTo = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
        includeLabs = true,
        includeAiSummaries = true,
        includeMedications = true,
        includeVisits = true,
        includeMeasurements = true,
        includeSymptomsNotes = false,
        recipient = "терапевт Смирнова",
        patientComment = comment,
        shareDays,
    };

    private async Task<DoctorReportDto> CreateAsync(HttpClient client, int? shareDays = 14)
    {
        var response = await client.PostAsJsonAsync("/api/doctor-reports", CreateBody(shareDays));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DoctorReportDto>(JsonOpts))!;
    }

    /// <summary>Клиент БЕЗ аутентификации — так открывает ссылку врач.</summary>
    private HttpClient Anonymous() => Factory.CreateClient();

    [Fact]
    public async Task Create_ThenDoctorOpensLinkAnonymously_AndOwnerSeesTheOpen()
    {
        var owner = ClientAs(FreshTelegramId());
        var report = await CreateAsync(owner);
        report.Link.Status.Should().Be(DoctorReportLinkStatus.Active);
        report.Link.Token.Should().NotBeNullOrEmpty();

        var doctor = Anonymous();
        var meta = await doctor.GetFromJsonAsync<PublicReportMeta>($"/api/public/doctor-reports/{report.Link.Token}", JsonOpts);
        meta!.Sections.Should().Contain("Жалобы и вопросы пациента");

        var pdf = await doctor.GetAsync($"/api/public/doctor-reports/{report.Link.Token}/pdf");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync(), 0, 4).Should().Be("%PDF");

        var list = await owner.GetFromJsonAsync<List<DoctorReportDto>>("/api/doctor-reports", JsonOpts);
        list!.Single(r => r.Id == report.Id).Link.ViewCount.Should().Be(1);
    }

    [Fact]
    public async Task PublicEndpoints_WithUnknownToken_Return404_NotAuthErrors()
    {
        var doctor = Anonymous();

        (await doctor.GetAsync("/api/public/doctor-reports/no-such-token")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await doctor.GetAsync("/api/public/doctor-reports/no-such-token/pdf")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OwnerEndpoints_RequireAuthentication()
    {
        (await Anonymous().GetAsync("/api/doctor-reports")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoke_MakesLinkDead_ButOwnerKeepsThePdf()
    {
        var owner = ClientAs(FreshTelegramId());
        var report = await CreateAsync(owner);

        var revoked = await owner.PostAsync($"/api/doctor-reports/{report.Id}/revoke", null);
        revoked.StatusCode.Should().Be(HttpStatusCode.OK);
        (await revoked.Content.ReadFromJsonAsync<DoctorReportDto>(JsonOpts))!.Link.Status.Should().Be(DoctorReportLinkStatus.Revoked);

        (await Anonymous().GetAsync($"/api/public/doctor-reports/{report.Link.Token}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var download = await owner.GetAsync($"/api/doctor-reports/{report.Id}/pdf");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentDisposition!.FileName.Should().StartWith("otchet-dlya-vracha-");
    }

    [Fact]
    public async Task Share_ExtendsActiveLink_AndReissuesAfterRevoke()
    {
        var owner = ClientAs(FreshTelegramId());
        var report = await CreateAsync(owner, shareDays: 7);

        var extended = await (await owner.PostAsJsonAsync($"/api/doctor-reports/{report.Id}/share", new { days = 30 }))
            .Content.ReadFromJsonAsync<DoctorReportDto>(JsonOpts);
        extended!.Link.Token.Should().Be(report.Link.Token);

        await owner.PostAsync($"/api/doctor-reports/{report.Id}/revoke", null);
        var reissued = await (await owner.PostAsJsonAsync($"/api/doctor-reports/{report.Id}/share", new { days = 14 }))
            .Content.ReadFromJsonAsync<DoctorReportDto>(JsonOpts);
        reissued!.Link.Token.Should().NotBe(report.Link.Token);
        (await Anonymous().GetAsync($"/api/public/doctor-reports/{reissued.Link.Token}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await owner.PostAsJsonAsync($"/api/doctor-reports/{report.Id}/share", new { days = 3 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_WithNoDataForPeriod_Returns422_AndInvalidInput400WithMessage()
    {
        var owner = ClientAs(FreshTelegramId());

        var empty = await owner.PostAsJsonAsync("/api/doctor-reports", CreateBody(comment: null));
        empty.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await empty.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("message").GetString().Should().NotBeNullOrEmpty();

        var invalid = await owner.PostAsJsonAsync("/api/doctor-reports", CreateBody(shareDays: 5));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Preview_ReturnsCounts()
    {
        var owner = ClientAs(FreshTelegramId());
        await owner.PostAsJsonAsync("/api/health-notes", new
        {
            kind = (int)HealthNoteKind.Note,
            occurredAt = DateTime.UtcNow.AddDays(-1),
            text = "заметка",
        });
        var from = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-1).ToString("yyyy-MM-dd");
        var to = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        var counts = await owner.GetFromJsonAsync<ReportCounts>($"/api/doctor-reports/preview?from={from}&to={to}", JsonOpts);

        counts!.DiaryEntries.Should().Be(1);
        counts.Analyses.Should().Be(0);
    }

    [Fact]
    public async Task OtherUser_CannotTouchSomeoneElsesReport()
    {
        var owner = ClientAs(FreshTelegramId());
        var stranger = ClientAs(FreshTelegramId());
        var report = await CreateAsync(owner);

        (await stranger.GetFromJsonAsync<List<DoctorReportDto>>("/api/doctor-reports", JsonOpts)).Should().BeEmpty();
        (await stranger.GetAsync($"/api/doctor-reports/{report.Id}/pdf")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.PostAsync($"/api/doctor-reports/{report.Id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.DeleteAsync($"/api/doctor-reports/{report.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_RemovesReport_AndTheLinkStopsWorking()
    {
        var owner = ClientAs(FreshTelegramId());
        var report = await CreateAsync(owner);

        (await owner.DeleteAsync($"/api/doctor-reports/{report.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Anonymous().GetAsync($"/api/public/doctor-reports/{report.Link.Token}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteAccount_ErasesReportsAndTheirPdfBlobs_AndKillsTheLink()
    {
        var owner = ClientAs(FreshTelegramId());
        var report = await CreateAsync(owner);

        string storageKey;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            storageKey = await db.FileAttachments.AsNoTracking()
                .Where(a => a.OwnerId == report.Id).Select(a => a.StorageKey).SingleAsync();
        }

        (await owner.PostAsJsonAsync("/api/account/delete", new { confirm = "DELETE" })).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.DoctorReports.AnyAsync(r => r.Id == report.Id)).Should().BeFalse();
            (await db.FileAttachments.AnyAsync(a => a.OwnerId == report.Id)).Should().BeFalse();
            var storage = scope.ServiceProvider.GetRequiredService<FamilyHub.Infrastructure.Storage.IFileStorage>();
            var act = async () => await storage.OpenReadAsync(storageKey);
            await act.Should().ThrowAsync<Minio.Exceptions.ObjectNotFoundException>("PDF отчёта удалён из хранилища");
        }
        (await Anonymous().GetAsync($"/api/public/doctor-reports/{report.Link.Token}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Export_ListsReportsWithoutTokens()
    {
        var owner = ClientAs(FreshTelegramId());
        var report = await CreateAsync(owner);

        var response = await owner.GetAsync("/api/account/export");

        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("doctor-reports.json")!.Open());
        var json = await reader.ReadToEndAsync();
        json.Should().Contain(report.Id.ToString()).And.Contain("Слабость и головные боли");
        json.Should().NotContain(report.Link.Token!, "токен публичной ссылки в экспорт не попадает");
    }

    // ---- Отчёт для члена семьи и подопечного ----

    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(Guid Id, string Code);
    private record PendingMemberDto(Guid UserId);

    private async Task<(Guid FamilyId, HttpClient Admin, HttpClient Member)> FamilyWithMemberAsync()
    {
        var admin = ClientAs(FreshTelegramId());
        var family = await (await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);
        var invite = await (await admin.PostAsJsonAsync($"/api/families/{family!.Id}/invites",
                new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 1, ExpiresAt: null)))
            .Content.ReadFromJsonAsync<CreateInviteResponseDto>(JsonOpts);
        var member = ClientAs(FreshTelegramId());
        await member.PostAsync($"/api/invites/{invite!.Code}/redeem", null);
        var pending = await (await admin.GetAsync($"/api/families/{family.Id}/pending"))
            .Content.ReadFromJsonAsync<List<PendingMemberDto>>(JsonOpts);
        await admin.PostAsync($"/api/families/{family.Id}/members/{pending!.Single().UserId}/approve", null);
        return (family.Id, admin, member);
    }

    private static object BodyFor(DoctorReportSubjectKind kind, Guid id, int? shareDays = 14) => new
    {
        periodFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-6).ToString("yyyy-MM-dd"),
        periodTo = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
        includeLabs = true,
        includeVisits = true,
        recipient = "кардиолог",
        shareDays,
        subjectKind = (int)kind,
        subjectId = id,
    };

    [Fact]
    public async Task ReportAboutFamilyMember_TheySeeIt_CanRevoke_CannotDelete_AndAccountErasureRemovesIt()
    {
        var (familyId, admin, member) = await FamilyWithMemberAsync();
        (await member.PostAsJsonAsync("/api/medical-records",
                new CreateMedicalRecordRequest(DateOnly.FromDateTime(DateTime.UtcNow), "Кардиолог", "ЭКГ", null, MedicalRecordKind.DoctorVisit)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await member.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var subjects = await admin.GetFromJsonAsync<List<DoctorReportSubjectDto>>("/api/doctor-reports/subjects", JsonOpts);
        var target = subjects!.Single(s => s.Kind == DoctorReportSubjectKind.User);

        var created = await admin.PostAsJsonAsync("/api/doctor-reports", BodyFor(DoctorReportSubjectKind.User, target.Id!.Value));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var report = (await created.Content.ReadFromJsonAsync<DoctorReportDto>(JsonOpts))!;

        var theirs = (await member.GetFromJsonAsync<List<DoctorReportDto>>("/api/doctor-reports", JsonOpts))!.Single();
        theirs.Id.Should().Be(report.Id);
        theirs.CanManage.Should().BeFalse();
        theirs.Recipient.Should().BeNull();
        (await member.GetAsync($"/api/doctor-reports/{report.Id}/pdf")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await member.DeleteAsync($"/api/doctor-reports/{report.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await member.PostAsync($"/api/doctor-reports/{report.Id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Anonymous().GetAsync($"/api/public/doctor-reports/{report.Link.Token}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await member.PostAsJsonAsync("/api/account/delete", new { confirm = "DELETE" })).StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.DoctorReports.AnyAsync(r => r.Id == report.Id)).Should().BeFalse("снимок данных пациента не переживает его аккаунт");
    }

    [Fact]
    public async Task ReportAboutDependent_AndStrangerCannotTarget()
    {
        var (familyId, admin, _) = await FamilyWithMemberAsync();
        var depResponse = await admin.PostAsJsonAsync($"/api/families/{familyId}/dependents",
            new CreateFamilyDependentRequest("Миша", "Поляков", null, Gender.Male, null, false, null));
        var dependentId = (await depResponse.Content.ReadFromJsonAsync<FamilyDependentDto>(JsonOpts))!.Id;
        (await admin.PostAsJsonAsync("/api/medical-records",
                new CreateMedicalRecordRequest(DateOnly.FromDateTime(DateTime.UtcNow), "Педиатр", "Осмотр", null,
                    MedicalRecordKind.DoctorVisit, FamilyDependentId: dependentId)))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await admin.PostAsJsonAsync("/api/doctor-reports", BodyFor(DoctorReportSubjectKind.Dependent, dependentId));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        (await created.Content.ReadFromJsonAsync<DoctorReportDto>(JsonOpts))!.SubjectName.Should().Be("Поляков Миша");

        var stranger = ClientAs(FreshTelegramId());
        (await stranger.PostAsJsonAsync("/api/doctor-reports", BodyFor(DoctorReportSubjectKind.Dependent, dependentId)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
