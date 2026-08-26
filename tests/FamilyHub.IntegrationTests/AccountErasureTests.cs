using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Критичные сценарии задачи 2.3: право на забвение (каскадное удаление всех ПДн)
/// и экспорт данных субъекта.
/// </summary>
public class AccountErasureTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(Guid Id, string Code);
    private record PendingMemberDto(Guid UserId);

    private async Task<(Guid FamilyId, HttpClient Admin, HttpClient Member, Guid MemberUserId)> CreateFamilyWithActiveMemberAsync()
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
        var memberUserId = pending!.Single().UserId;
        await admin.PostAsync($"/api/families/{family.Id}/members/{memberUserId}/approve", null);
        return (family.Id, admin, member, memberUserId);
    }

    private static MultipartFormDataContent BuildUpload(string text)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "scan.txt");
        return content;
    }

    [Fact]
    public async Task DeleteAccount_ErasesAllPersonalData_IncludingRecordsFilesAndShares()
    {
        var (familyId, admin, member, memberUserId) = await CreateFamilyWithActiveMemberAsync();

        // Член семьи создаёт медзапись с вложением и расшаривает семье.
        var record = await (await member.PostAsJsonAsync("/api/medical-records",
                new CreateMedicalRecordRequest(DateOnly.FromDateTime(DateTime.UtcNow), "Врач", "Диагноз", null)))
            .Content.ReadFromJsonAsync<MedicalRecordDto>(JsonOpts);
        (await member.PostAsync($"/api/medical-records/{record!.Id}/attachments", BuildUpload("private-bytes")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await member.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        string storageKey;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            storageKey = await db.FileAttachments.AsNoTracking()
                .Where(a => a.OwnerId == record.Id).Select(a => a.StorageKey).SingleAsync();
        }

        // Удаление аккаунта: без подтверждения — 400, с подтверждением — успех.
        (await member.PostAsJsonAsync("/api/account/delete", new { confirm = "нет" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await member.PostAsJsonAsync("/api/account/delete", new { confirm = "DELETE" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Users.AnyAsync(u => u.Id == memberUserId)).Should().BeFalse("User удалён");
            (await db.MedicalRecords.AnyAsync(r => r.OwnerUserId == memberUserId)).Should().BeFalse("медзаписи удалены");
            (await db.FileAttachments.AnyAsync(a => a.OwnerId == record.Id)).Should().BeFalse("строки вложений удалены");
            (await db.FamilyMedicalShares.AnyAsync(s => s.OwnerUserId == memberUserId)).Should().BeFalse("шары удалены");
            (await db.FamilyMembers.AnyAsync(m => m.UserId == memberUserId)).Should().BeFalse("членство удалено");
            (await db.Set<FamilyHub.Domain.Entities.UserConsent>().AnyAsync(c => c.UserId == memberUserId))
                .Should().BeTrue("факт согласия — юридическое доказательство, переживает удаление");

            var storage = scope.ServiceProvider.GetRequiredService<FamilyHub.Infrastructure.Storage.IFileStorage>();
            var act = async () => await storage.OpenReadAsync(storageKey);
            // MinIO — единственная реализация IFileStorage (LocalFileStorage упразднён): отсутствующий
            // объект даёт ObjectNotFoundException из клиента MinIO, а не FileNotFoundException.
            await act.Should().ThrowAsync<Minio.Exceptions.ObjectNotFoundException>("блоб удалён из хранилища");
        }

        // Повторный вход с тем же TelegramId создаёт НОВЫЙ пустой аккаунт (get-or-create) —
        // старых данных не существует; семья admin'а живёт дальше без удалённого участника.
        (await admin.GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteAccount_LastAdminOfFamilyWithMembers_Returns409WithFamilyList()
    {
        var (_, admin, _, _) = await CreateFamilyWithActiveMemberAsync();

        var response = await admin.PostAsJsonAsync("/api/account/delete", new { confirm = "DELETE" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("code").GetString().Should().Be("last_admin");
        body.GetProperty("families").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task DeleteAccount_SoleMemberFamily_IsDeletedEntirely()
    {
        var soloAdmin = ClientAs(FreshTelegramId());
        var family = await (await soloAdmin.PostAsJsonAsync("/api/families", new { Name = $"Соло {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);

        (await soloAdmin.PostAsJsonAsync("/api/account/delete", new { confirm = "DELETE" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Families.AnyAsync(f => f.Id == family!.Id)).Should().BeFalse("семья без других членов удалена целиком");
    }

    [Fact]
    public async Task Export_ContainsDecryptedRecords_AndAttachmentBytes()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await (await owner.PostAsJsonAsync("/api/medical-records",
                new CreateMedicalRecordRequest(DateOnly.FromDateTime(DateTime.UtcNow), null, "Диагноз-текст", null)))
            .Content.ReadFromJsonAsync<MedicalRecordDto>(JsonOpts);
        (await owner.PostAsync($"/api/medical-records/{record!.Id}/attachments", BuildUpload("export-file-bytes")))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await owner.GetAsync("/api/account/export");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");

        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        zip.Entries.Select(e => e.FullName).Should().Contain(["profile.json", "consents.json", "families.json", "medical-records.json"]);

        using var recordsReader = new StreamReader(zip.GetEntry("medical-records.json")!.Open());
        var recordsJson = await recordsReader.ReadToEndAsync();
        recordsJson.Should().Contain("Диагноз-текст", "экспорт содержит расшифрованные поля");

        var attachmentEntry = zip.Entries.Single(e => e.FullName.StartsWith("attachments/"));
        using var attachmentReader = new StreamReader(attachmentEntry.Open());
        (await attachmentReader.ReadToEndAsync()).Should().Be("export-file-bytes", "вложение расшифровано");

        using var consentsReader = new StreamReader(zip.GetEntry("consents.json")!.Open());
        (await consentsReader.ReadToEndAsync()).Should().Contain("version");
    }
}
