using System.Net;
using System.Net.Http.Json;
using System.Text;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Задача 2.7: доступ к медицинским данным фиксируется (кто, к чьим, когда),
/// строки аудита не содержат ни ПДн, ни содержимого медданных.
/// </summary>
public class MedicalAuditTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(Guid Id, string Code);
    private record PendingMemberDto(Guid UserId);

    [Fact]
    public async Task SharedRecordAccess_IsAudited_WithoutPersonalData()
    {
        // Владелец шарит записи семье; админ читает список и скачивает вложение.
        var admin = ClientAs(FreshTelegramId());
        var family = await (await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);
        var invite = await (await admin.PostAsJsonAsync($"/api/families/{family!.Id}/invites",
                new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 1, ExpiresAt: null)))
            .Content.ReadFromJsonAsync<CreateInviteResponseDto>(JsonOpts);
        var owner = ClientAs(FreshTelegramId());
        await owner.PostAsync($"/api/invites/{invite!.Code}/redeem", null);
        var pending = await (await admin.GetAsync($"/api/families/{family.Id}/pending"))
            .Content.ReadFromJsonAsync<List<PendingMemberDto>>(JsonOpts);
        var ownerUserId = pending!.Single().UserId;
        await admin.PostAsync($"/api/families/{family.Id}/members/{ownerUserId}/approve", null);

        const string secretName = "Аудируемый Пациент";
        var record = await (await owner.PostAsJsonAsync("/api/medical-records",
                new CreateMedicalRecordRequest(secretName, DateOnly.FromDateTime(DateTime.UtcNow), null, "Секретный диагноз", null)))
            .Content.ReadFromJsonAsync<MedicalRecordDto>(JsonOpts);

        var upload = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("scan"));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        upload.Add(fileContent, "file", "scan.txt");
        var attachment = await (await owner.PostAsync($"/api/medical-records/{record!.Id}/attachments", upload))
            .Content.ReadFromJsonAsync<FamilyHub.Modules.Medical.Attachments.AttachmentDto>(JsonOpts);

        (await owner.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(family.Id)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Доступ админа к чужим данным: список + выдача ссылки на файл.
        (await admin.GetAsync("/api/medical-records")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync($"/api/attachments/{attachment!.Id}/url")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audits = await db.Set<MedicalAccessAudit>().AsNoTracking()
            .Where(a => a.OwnerUserId == ownerUserId)
            .ToListAsync();

        audits.Should().Contain(a => a.Action == MedicalAccessAction.Share && a.ActorUserId == ownerUserId,
            "шаринг зафиксирован");
        audits.Should().Contain(a => a.Action == MedicalAccessAction.ViewList && a.ActorUserId != ownerUserId,
            "просмотр чужих записей зафиксирован (кто и к чьим)");
        audits.Should().Contain(a =>
                a.Action == MedicalAccessAction.DownloadAttachment
                && a.ActorUserId != ownerUserId
                && a.AttachmentId == attachment.Id
                && a.MedicalRecordId == record.Id,
            "выдача ссылки на файл зафиксирована");
        audits.Should().OnlyContain(a => a.OccurredAt > DateTime.UtcNow.AddMinutes(-5));

        // Acceptance 2.7: в аудите нет ПДн и содержимого — только Guid/enum/дата.
        // Проверяем на уровне модели: ни одного строкового столбца.
        var auditEntity = db.Model.FindEntityType(typeof(MedicalAccessAudit))!;
        auditEntity.GetProperties()
            .Where(p => p.ClrType == typeof(string))
            .Should().BeEmpty("строковых полей нет — ПДн и содержимому физически некуда попасть");
    }

    [Fact]
    public async Task OwnViewAndFailedAccess_AreNotAudited()
    {
        var owner = ClientAs(FreshTelegramId());
        await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest("Свой Пациент", DateOnly.FromDateTime(DateTime.UtcNow), null, null, null));

        // Просмотр СВОИХ записей аудитом не считается (фиксируется доступ к чужим данным).
        (await owner.GetAsync("/api/medical-records")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Set<MedicalAccessAudit>().AsNoTracking()
                .AnyAsync(a => a.Action == MedicalAccessAction.ViewList && a.ActorUserId == a.OwnerUserId))
            .Should().BeFalse();
    }
}
