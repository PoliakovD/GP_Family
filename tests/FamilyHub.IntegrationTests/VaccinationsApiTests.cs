using System.Net;
using System.Net.Http.Json;
using FamilyHub.Api.Features.Dependents;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.Vaccinations;
using FamilyHub.Modules.Medical.MedicationCourses;
using FamilyHub.Modules.Medical.Vaccinations;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Прививки (ADR-0016): изоляция по владельцу — тот же трёхканальный доступ, что у курсов
/// приёма лекарств (свой/подопечный семьи/наблюдаемый взрослый только на чтение).</summary>
public class VaccinationsApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(Guid Id, string Code);
    private record PendingMemberDto(Guid UserId);
    private record MeDto(Guid UserId);

    private async Task<(Guid FamilyId, HttpClient Admin, HttpClient Member)> CreateFamilyWithActiveMemberAsync()
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

    private static object OneDose(string subjectKind, Guid subjectId, string seriesCode, int doseIndex, DateOnly date) => new
    {
        subjectKind, subjectId, seriesCode, doseIndex, customName = (string?)null, vaccineName = (string?)null,
        kind = (int)VaccinationKind.Done, date, datePrecision = (int)VaccinationDatePrecision.Day,
        certificateId = (Guid?)null, requestWellbeingCheck = false,
    };

    [Fact]
    public async Task CreateForSelf_ThenPersonSchedule_ShowsDone()
    {
        var owner = ClientAs(FreshTelegramId());
        var me = await owner.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);
        await SetBirthDateAsync(owner, new DateOnly(2020, 1, 1));

        var created = await owner.PostAsJsonAsync(
            "/api/vaccinations", OneDose(VaccinationSubjects.UserKind, me!.UserId, "mmr", 0, new DateOnly(2021, 1, 1)));
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        var schedule = await owner.GetFromJsonAsync<PersonScheduleDto>(
            $"/api/vaccinations/people/{VaccinationSubjects.UserKind}/{me.UserId}", JsonOpts);
        schedule!.ByAge.Should().Contain(i => i.SeriesCode == "mmr" && i.DoseIndex == 0 && i.Status == VaccinationStatus.Done);
    }

    [Fact]
    public async Task Outsider_CannotSeeOrWrite_DependentVaccinations()
    {
        var (familyId, admin, _) = await CreateFamilyWithActiveMemberAsync();
        var dependent = await (await admin.PostAsJsonAsync($"/api/families/{familyId}/dependents",
                new CreateFamilyDependentRequest("Ребёнок", null, null, Gender.Male, new DateOnly(2019, 1, 1), false, null)))
            .Content.ReadFromJsonAsync<FamilyDependentDto>(JsonOpts);

        var outsider = ClientAs(FreshTelegramId());
        var createResponse = await outsider.PostAsJsonAsync(
            "/api/vaccinations", OneDose(VaccinationSubjects.DependentKind, dependent!.Id, "bcg", 0, new DateOnly(2019, 1, 5)));
        createResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var readResponse = await outsider.GetAsync($"/api/vaccinations/people/{VaccinationSubjects.DependentKind}/{dependent.Id}");
        readResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FamilyMember_BulkMarksDependent_MemberAndAdminBothSeeIt()
    {
        var (familyId, admin, member) = await CreateFamilyWithActiveMemberAsync();
        var dependent = await (await admin.PostAsJsonAsync($"/api/families/{familyId}/dependents",
                new CreateFamilyDependentRequest("Ребёнок", null, null, Gender.Female, new DateOnly(2019, 1, 1), false, null)))
            .Content.ReadFromJsonAsync<FamilyDependentDto>(JsonOpts);

        var bulk = await member.PostAsJsonAsync("/api/vaccinations/bulk", new
        {
            subjectKind = VaccinationSubjects.DependentKind,
            subjectId = dependent!.Id,
            items = new[]
            {
                new { seriesCode = "bcg", doseIndex = 0, kind = (int)VaccinationKind.Done, date = (DateOnly?)new DateOnly(2019, 1, 5), datePrecision = (int?)VaccinationDatePrecision.Day },
                new { seriesCode = "bcg", doseIndex = 1, kind = (int)VaccinationKind.Unknown, date = (DateOnly?)null, datePrecision = (int?)null },
            },
            certificateId = (Guid?)null,
        });
        bulk.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await bulk.Content.ReadFromJsonAsync<BulkMarkResultDto>(JsonOpts);
        result!.Saved.Should().Be(2);

        var forAdmin = await admin.GetFromJsonAsync<PersonScheduleDto>(
            $"/api/vaccinations/people/{VaccinationSubjects.DependentKind}/{dependent.Id}", JsonOpts);
        forAdmin!.ByAge.Should().Contain(i => i.SeriesCode == "bcg" && i.DoseIndex == 0 && i.Status == VaccinationStatus.Done);
        forAdmin.ByAge.Should().Contain(i => i.SeriesCode == "bcg" && i.DoseIndex == 1 && i.Status == VaccinationStatus.NoData);
    }

    [Fact]
    public async Task Watcher_OfAdult_CanReadButWriteIsForbidden()
    {
        // ADR-0017: доступ на чтение чужих прививок — отдельный грант, с кем есть общая активная семья
        // (HealthShareService.SetAsync) — владелец и зритель здесь состоят в одной семье, admin=owner,
        // member=viewer. В отличие от курсов приёма, «мои наблюдатели» (my-watchers) прививки не открывают.
        var (_, owner, watcher) = await CreateFamilyWithActiveMemberAsync();
        var ownerMe = await owner.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);
        await SetBirthDateAsync(owner, new DateOnly(1990, 1, 1));
        var watcherMe = await watcher.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);

        (await owner.PutAsJsonAsync($"/api/health-shares/mine/{watcherMe!.UserId}", new { categories = (int)HealthShareCategory.Vaccinations }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var readResponse = await watcher.GetAsync($"/api/vaccinations/people/{VaccinationSubjects.UserKind}/{ownerMe!.UserId}");
        readResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var writeResponse = await watcher.PostAsJsonAsync(
            "/api/vaccinations", OneDose(VaccinationSubjects.UserKind, ownerMe.UserId, "mmr", 0, new DateOnly(2021, 1, 1)));
        writeResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MyWatchers_Alone_DoesNotGrantVaccinationAccess()
    {
        // ADR-0017: «мои наблюдатели» (курсы приёма) больше не открывают прививки автоматически —
        // это отдельная категория гранта.
        var (_, owner, watcher) = await CreateFamilyWithActiveMemberAsync();
        var ownerMe = await owner.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);
        await SetBirthDateAsync(owner, new DateOnly(1990, 1, 1));
        var watcherMe = await watcher.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);

        (await owner.PutAsJsonAsync("/api/medication-reminders/my-watchers", new SetMyWatchersRequest([watcherMe!.UserId])))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var readResponse = await watcher.GetAsync($"/api/vaccinations/people/{VaccinationSubjects.UserKind}/{ownerMe!.UserId}");
        readResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ByOwner_Succeeds_ByOutsider_IsNotFound()
    {
        var owner = ClientAs(FreshTelegramId());
        var me = await owner.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);
        await SetBirthDateAsync(owner, new DateOnly(1990, 1, 1));

        var created = await (await owner.PostAsJsonAsync(
                "/api/vaccinations", OneDose(VaccinationSubjects.UserKind, me!.UserId, "mmr", 0, new DateOnly(2021, 1, 1))))
            .Content.ReadFromJsonAsync<ScheduleItemDto>(JsonOpts);

        var outsider = ClientAs(FreshTelegramId());
        (await outsider.DeleteAsync($"/api/vaccinations/{created!.RecordId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.DeleteAsync($"/api/vaccinations/{created.RecordId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CertificatePdf_ForOwnerWithNoData_StillReturnsOk()
    {
        var owner = ClientAs(FreshTelegramId());
        var me = await owner.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);
        await SetBirthDateAsync(owner, new DateOnly(1990, 1, 1));

        var response = await owner.GetAsync($"/api/vaccinations/people/{VaccinationSubjects.UserKind}/{me!.UserId}/certificate.pdf");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
    }

    /// <summary>Профиль пока не выставляет свободный PUT даты рождения в этих тестах отдельным
    /// эндпоинтом — правим напрямую в БД через фабрику (тот же приём, что и другие ApiTests, которым
    /// нужно засеять состояние, недостижимое обычным потоком онбординга).</summary>
    private async Task SetBirthDateAsync(HttpClient client, DateOnly birthDate)
    {
        var me = await client.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FamilyHub.Infrastructure.Persistence.AppDbContext>();
        var user = await db.Users.FindAsync(me!.UserId);
        user!.BirthDate = birthDate;
        await db.SaveChangesAsync();
    }
}
