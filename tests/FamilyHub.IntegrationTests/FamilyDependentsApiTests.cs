using System.Net;
using System.Net.Http.Json;
using System.Text;
using FamilyHub.Api.Features.Dependents;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// FamilyDependent (задача «Близкие и питомцы»): Create/Update — любой активный Member,
/// Delete — только Admin (осознанное отличие от Medkit/Birthday), с каскадным удалением
/// связанных MedicalRecord и физической чисткой их вложений из MinIO.
/// </summary>
public class FamilyDependentsApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(Guid Id, string Code);
    private record PendingMemberDto(Guid UserId);

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

    [Fact]
    public async Task Create_AsActiveMember_Succeeds_AndTogglesPetSpeciesByIsPet()
    {
        var (familyId, admin, member) = await CreateFamilyWithActiveMemberAsync();

        var petResponse = await member.PostAsJsonAsync($"/api/families/{familyId}/dependents",
            new CreateFamilyDependentRequest("Барсик", null, null, Gender.Male, null, true, "кот"));
        petResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var pet = await petResponse.Content.ReadFromJsonAsync<FamilyDependentDto>(JsonOpts);
        pet!.IsPet.Should().BeTrue();
        pet.PetSpecies.Should().Be("кот");

        var childResponse = await admin.PostAsJsonAsync($"/api/families/{familyId}/dependents",
            new CreateFamilyDependentRequest("Ваня", "Иванов", null, Gender.Male, new DateOnly(2018, 5, 1), false, "кот"));
        var child = await childResponse.Content.ReadFromJsonAsync<FamilyDependentDto>(JsonOpts);
        child!.IsPet.Should().BeFalse();
        child.PetSpecies.Should().BeNull("вид животного не должен просочиться, если IsPet == false");

        var list = await (await admin.GetAsync($"/api/families/{familyId}/dependents"))
            .Content.ReadFromJsonAsync<List<FamilyDependentDto>>(JsonOpts);
        list.Should().HaveCount(2);
    }

    [Fact]
    public async Task Create_NotFamilyMember_Forbidden()
    {
        var admin = ClientAs(FreshTelegramId());
        var family = await (await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);
        var outsider = ClientAs(FreshTelegramId());

        var response = await outsider.PostAsJsonAsync($"/api/families/{family!.Id}/dependents",
            new CreateFamilyDependentRequest("Барсик", null, null, Gender.Male, null, true, "кот"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delete_AsMember_Forbidden_AsAdmin_Succeeds()
    {
        var (familyId, admin, member) = await CreateFamilyWithActiveMemberAsync();
        var dependent = await (await admin.PostAsJsonAsync($"/api/families/{familyId}/dependents",
                new CreateFamilyDependentRequest("Барсик", null, null, Gender.Male, null, true, "кот")))
            .Content.ReadFromJsonAsync<FamilyDependentDto>(JsonOpts);

        var memberAttempt = await member.DeleteAsync($"/api/dependents/{dependent!.Id}");
        memberAttempt.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var adminAttempt = await admin.DeleteAsync($"/api/dependents/{dependent.Id}");
        adminAttempt.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_UnknownDependent_NotFound()
    {
        var admin = ClientAs(FreshTelegramId());

        var response = await admin.DeleteAsync($"/api/dependents/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_CascadesMedicalRecordsAndAttachments_AndRemovesBlobFromMinio()
    {
        var (familyId, admin, member) = await CreateFamilyWithActiveMemberAsync();
        var dependent = await (await admin.PostAsJsonAsync($"/api/families/{familyId}/dependents",
                new CreateFamilyDependentRequest("Барсик", null, null, Gender.Male, null, true, "кот")))
            .Content.ReadFromJsonAsync<FamilyDependentDto>(JsonOpts);

        var record = await (await member.PostAsJsonAsync("/api/medical-records",
                new CreateMedicalRecordRequest(
                    "Барсик", DateOnly.FromDateTime(DateTime.UtcNow), "Ветеринар", null, null,
                    FamilyDependentId: dependent!.Id)))
            .Content.ReadFromJsonAsync<MedicalRecordDto>(JsonOpts);
        record!.FamilyDependentId.Should().Be(dependent.Id);

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("vet-scan-bytes"));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "scan.pdf");
        (await member.PostAsync($"/api/medical-records/{record.Id}/attachments", content))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        string storageKey;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            storageKey = await db.FileAttachments.AsNoTracking()
                .Where(a => a.OwnerId == record.Id).Select(a => a.StorageKey).SingleAsync();
        }

        // Активный участник семьи подопечного видит запись автоматически, без L1-шаринга.
        var beforeDelete = await (await admin.GetAsync("/api/medical-records"))
            .Content.ReadFromJsonAsync<List<MedicalRecordDto>>(JsonOpts);
        beforeDelete.Should().ContainSingle(r => r.Id == record.Id);

        (await admin.DeleteAsync($"/api/dependents/{dependent.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.MedicalRecords.AnyAsync(r => r.Id == record.Id)).Should().BeFalse("запись каскадно удалена вместе с подопечным");
            (await db.FileAttachments.AnyAsync(a => a.OwnerId == record.Id)).Should().BeFalse("строка вложения удалена явно");

            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            var act = async () => await storage.OpenReadAsync(storageKey);
            await act.Should().ThrowAsync<Minio.Exceptions.ObjectNotFoundException>("блоб физически удалён из MinIO");
        }
    }
}
