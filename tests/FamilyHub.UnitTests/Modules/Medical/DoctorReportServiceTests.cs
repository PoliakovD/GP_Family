using System.Text;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Previews;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.DoctorReports;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkiaSharp;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class DoctorReportServiceTests : SqliteTestBase
{
    private readonly IFileStorage _storage = Substitute.For<IFileStorage>();
    private readonly IGotenbergConverter _gotenberg = Substitute.For<IGotenbergConverter>();
    private readonly IEncryptionKeyRing _keyRing =
        new EncryptionKeyRing(new EncryptionOptions { MasterKey = DesignTimeDbContextFactory.DevMasterKey });
    private readonly Dictionary<string, byte[]> _blobs = [];
    private readonly DoctorReportService _sut;
    private readonly User _me;

    public DoctorReportServiceTests()
    {
        _storage.SaveAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                using var copy = new MemoryStream();
                call.Arg<Stream>().CopyTo(copy);
                _blobs[call.ArgAt<string>(0)] = copy.ToArray();
                return Task.FromResult(call.ArgAt<string>(0));
            });
        _storage.OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<Stream>(new MemoryStream(_blobs[call.ArgAt<string>(0)])));
        _storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => { _blobs.Remove(call.ArgAt<string>(0)); return Task.CompletedTask; });

        _gotenberg.ConvertHtmlToPdfAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<byte[]?>(TwoPagePdf()));

        _sut = new DoctorReportService(
            Db, new DoctorReportDataCollector(Db), _gotenberg, new AesGcmFileCipher(_keyRing), _storage, _keyRing,
            new MedicalAuditWriter(Db), NullLogger<DoctorReportService>.Instance);

        _me = Db.AddUser();
        _me.LastName = "Поляков";
        _me.FirstName = "Даниил";
        _me.BirthDate = new DateOnly(1998, 5, 4);
        _me.Gender = Gender.Male;
        Db.SaveChanges();
    }

    private static byte[] TwoPagePdf()
    {
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            for (var i = 0; i < 2; i++)
            {
                using var canvas = document.BeginPage(400, 300);
                using var paint = new SKPaint { Color = SKColors.Black, TextSize = 18 };
                canvas.DrawText($"page {i}", 20, 40, paint);
                document.EndPage();
            }
            document.Close();
        }
        return stream.ToArray();
    }

    private static CreateDoctorReportRequest Request(
        int? shareDays = null, string? comment = "Слабость и головные боли", string? recipient = null,
        DateOnly? from = null, DateOnly? to = null, bool labs = true) =>
        new(from ?? DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-6), to ?? DateOnly.FromDateTime(DateTime.UtcNow),
            labs, true, true, true, true, false, recipient, comment, shareDays);

    private async Task<DoctorReportDto> CreateAsync(int? shareDays = null, User? owner = null)
    {
        var (result, item, error) = await _sut.CreateAsync((owner ?? _me).Id, Request(shareDays));
        result.Should().Be(DoctorReportResult.Success, error);
        return item!;
    }

    private DoctorReport Row(Guid id) => Db.DoctorReports.AsNoTracking().Single(r => r.Id == id);

    private List<MedicalAccessAudit> Audits(MedicalAccessAction action) =>
        Db.Set<MedicalAccessAudit>().AsNoTracking().Where(a => a.Action == action).ToList();

    [Fact]
    public async Task Create_StoresEncryptedPdf_AttachmentAndRow()
    {
        var dto = await CreateAsync();

        dto.PageCount.Should().Be(2);
        dto.BlockCount.Should().Be(5);
        dto.Link.Status.Should().Be(DoctorReportLinkStatus.None);

        var attachment = Db.FileAttachments.AsNoTracking().Single(a => a.OwnerId == dto.Id);
        attachment.OwnerType.Should().Be(FileOwnerType.DoctorReport);
        attachment.IsEncrypted.Should().BeTrue();
        attachment.KeyId.Should().Be(_keyRing.ActiveKeyId);
        attachment.PreviewStatus.Should().Be(AttachmentPreviewStatus.Unsupported);
        attachment.StorageKey.Should().Be(StorageKeyFactory.Create(attachment.Id));

        var blob = _blobs[attachment.StorageKey];
        Encoding.ASCII.GetString(blob, 0, Math.Min(blob.Length, 8)).Should().NotContain("%PDF", "в хранилище только шифротекст");
        Audits(MedicalAccessAction.DoctorReportCreated).Should().ContainSingle(a => a.ActorUserId == _me.Id);
    }

    [Fact]
    public async Task Create_WithShareDays_IssuesActiveLink_TokenStoredHashedAndEncrypted()
    {
        var dto = await CreateAsync(shareDays: 14);

        dto.Link.Status.Should().Be(DoctorReportLinkStatus.Active);
        dto.Link.Token.Should().HaveLength(43).And.MatchRegex("^[A-Za-z0-9_-]+$");
        dto.Link.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(14), TimeSpan.FromMinutes(1));
        Row(dto.Id).ShareTokenHash.Should().Be(TokenHasher.Hash(dto.Link.Token!));

        var conn = Db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ShareToken FROM DoctorReports";
        ((string)(await cmd.ExecuteScalarAsync())!).Should().StartWith("enc:").And.NotContain(dto.Link.Token!);
        Audits(MedicalAccessAction.DoctorReportLinkIssued).Should().HaveCount(1);
    }

    [Fact]
    public async Task Create_PatientCommentAndRecipient_AreEncryptedAtRest()
    {
        await _sut.CreateAsync(_me.Id, Request(comment: "Секретная жалоба", recipient: "терапевт Смирнова"));

        var conn = Db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PatientComment || '|' || Recipient || '|' || PatientSnapshotJson FROM DoctorReports";
        var raw = (string)(await cmd.ExecuteScalarAsync())!;
        raw.Should().NotContain("Секретная").And.NotContain("Смирнова").And.NotContain("Поляков");
    }

    [Theory]
    [InlineData("from-after-to")]
    [InlineData("too-long")]
    [InlineData("future")]
    [InlineData("no-blocks")]
    [InlineData("comment-too-long")]
    [InlineData("bad-share-days")]
    public async Task Create_InvalidInput_IsRejected_WithoutCallingPdfServiceOrStoring(string scenario)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var request = scenario switch
        {
            "from-after-to" => Request(from: today, to: today.AddDays(-5)),
            "too-long" => Request(from: today.AddYears(-3), to: today),
            "future" => Request(to: today.AddDays(10)),
            "no-blocks" => Request() with { IncludeLabs = false, IncludeAiSummaries = false, IncludeMedications = false, IncludeVisits = false, IncludeMeasurements = false },
            "comment-too-long" => Request(comment: new string('я', DoctorReportService.MaxCommentLength + 1)),
            _ => Request(shareDays: 5),
        };

        var (result, item, error) = await _sut.CreateAsync(_me.Id, request);

        result.Should().Be(DoctorReportResult.Invalid);
        item.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
        Db.DoctorReports.Should().BeEmpty();
        await _gotenberg.DidNotReceiveWithAnyArgs().ConvertHtmlToPdfAsync(default!, default, default);
    }

    [Fact]
    public async Task Create_WithoutAnyData_ReturnsNoData()
    {
        var (result, _, _) = await _sut.CreateAsync(_me.Id, Request(comment: null));

        result.Should().Be(DoctorReportResult.NoData);
        Db.DoctorReports.Should().BeEmpty();
        _blobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WhenPdfServiceUnavailable_StoresNothing()
    {
        _gotenberg.ConvertHtmlToPdfAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<byte[]?>(null));

        var (result, item, error) = await _sut.CreateAsync(_me.Id, Request());

        result.Should().Be(DoctorReportResult.PdfUnavailable);
        item.Should().BeNull();
        error.Should().Contain("недоступен");
        Db.DoctorReports.Should().BeEmpty();
        Db.FileAttachments.Should().BeEmpty();
        _blobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_OverLimit_IsRejected()
    {
        for (var i = 0; i < DoctorReportService.MaxReportsPerUser; i++)
            Db.DoctorReports.Add(new DoctorReport { Id = Guid.NewGuid(), OwnerUserId = _me.Id, CreatedAt = DateTime.UtcNow, PatientSnapshotJson = "{}" });
        Db.SaveChanges();

        var (result, _, _) = await _sut.CreateAsync(_me.Id, Request());

        result.Should().Be(DoctorReportResult.TooMany);
    }

    [Fact]
    public async Task Share_OnActiveLink_ExtendsSameToken()
    {
        var dto = await CreateAsync(shareDays: 7);

        var (result, extended, _) = await _sut.ShareAsync(_me.Id, dto.Id, 30);

        result.Should().Be(DoctorReportResult.Success);
        extended!.Link.Token.Should().Be(dto.Link.Token);
        extended.Link.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(30), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Share_WhenNoLinkOrExpired_IssuesNewToken_AndOldOneDies()
    {
        var dto = await CreateAsync(shareDays: 7);
        var oldToken = dto.Link.Token!;
        var row = Db.DoctorReports.Single();
        row.ShareExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        row.ShareViewCount = 3;
        Db.SaveChanges();

        var (_, renewed, _) = await _sut.ShareAsync(_me.Id, dto.Id, 14);

        renewed!.Link.Status.Should().Be(DoctorReportLinkStatus.Active);
        renewed.Link.Token.Should().NotBe(oldToken);
        renewed.Link.ViewCount.Should().Be(0, "счётчик открытий новой ссылки начинается с нуля");
        (await _sut.GetPublicMetaAsync(oldToken)).Should().BeNull();
        (await _sut.GetPublicMetaAsync(renewed.Link.Token!)).Should().NotBeNull();
    }

    [Fact]
    public async Task Share_OnlyAllowedDurations()
    {
        var dto = await CreateAsync();

        (await _sut.ShareAsync(_me.Id, dto.Id, 3)).Result.Should().Be(DoctorReportResult.Invalid);
        (await _sut.ShareAsync(_me.Id, dto.Id, 365)).Result.Should().Be(DoctorReportResult.Invalid);
        foreach (var days in DoctorReportService.AllowedShareDays)
            (await _sut.ShareAsync(_me.Id, dto.Id, days)).Result.Should().Be(DoctorReportResult.Success);
    }

    [Fact]
    public async Task Revoke_KillsLink_AndIsIdempotent()
    {
        var dto = await CreateAsync(shareDays: 14);
        var token = dto.Link.Token!;

        var (result, revoked) = await _sut.RevokeAsync(_me.Id, dto.Id);

        result.Should().Be(DoctorReportResult.Success);
        revoked!.Link.Status.Should().Be(DoctorReportLinkStatus.Revoked);
        revoked.Link.Token.Should().BeNull();
        revoked.Link.RevokedAt.Should().NotBeNull();
        Row(dto.Id).ShareTokenHash.Should().BeNull();
        (await _sut.GetPublicMetaAsync(token)).Should().BeNull();
        (await _sut.OpenPublicPdfAsync(token)).Should().BeNull();

        var again = await _sut.RevokeAsync(_me.Id, dto.Id);
        again.Result.Should().Be(DoctorReportResult.Success);
        Audits(MedicalAccessAction.DoctorReportLinkRevoked).Should().HaveCount(1, "повторный отзыв ничего не меняет");
    }

    [Fact]
    public async Task PublicMeta_ShowsSnapshotOnly_WithoutRecipientOrComment()
    {
        var (_, dto, _) = await _sut.CreateAsync(_me.Id, Request(shareDays: 14, recipient: "терапевт Смирнова", comment: "Слабость"));
        _me.LastName = "Изменённый"; // профиль поменяли после формирования
        Db.SaveChanges();

        var meta = await _sut.GetPublicMetaAsync(dto!.Link.Token!);

        meta!.PatientName.Should().Be("Поляков Даниил");
        meta.Sex.Should().Be("м");
        meta.Age.Should().BeGreaterThan(20);
        meta.BirthDate.Should().Be(new DateOnly(1998, 5, 4));
        meta.PageCount.Should().Be(2);
        meta.Sections.Should().Contain("Жалобы и вопросы пациента");
        meta.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(14), TimeSpan.FromMinutes(1));
        System.Text.Json.JsonSerializer.Serialize(meta).Should().NotContain("Смирнова").And.NotContain("Слабость");
    }

    [Fact]
    public async Task PublicPdf_ReturnsDecryptedDocument_CountsViewAndAuditsAnonymously()
    {
        var dto = await CreateAsync(shareDays: 14);

        await using var first = (await _sut.OpenPublicPdfAsync(dto.Link.Token!))!.Value.Content;
        using var bytes = new MemoryStream();
        await first.CopyToAsync(bytes);
        Encoding.ASCII.GetString(bytes.ToArray(), 0, 4).Should().Be("%PDF");
        await (await _sut.OpenPublicPdfAsync(dto.Link.Token!))!.Value.Content.DisposeAsync();

        var row = Row(dto.Id);
        row.ShareViewCount.Should().Be(2);
        row.ShareLastViewedAt.Should().NotBeNull();
        var views = Audits(MedicalAccessAction.DoctorReportViewed);
        views.Should().HaveCount(2);
        views.Should().OnlyContain(a => a.ActorUserId == Guid.Empty && a.OwnerUserId == _me.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task PublicAccess_WithUnknownToken_IsNull(string token)
    {
        await CreateAsync(shareDays: 14);

        (await _sut.GetPublicMetaAsync(token)).Should().BeNull();
        (await _sut.OpenPublicPdfAsync(token)).Should().BeNull();
        Audits(MedicalAccessAction.DoctorReportViewed).Should().BeEmpty();
    }

    [Fact]
    public async Task PublicAccess_AfterExpiry_IsNull_LikeAnUnknownToken()
    {
        var dto = await CreateAsync(shareDays: 7);
        var row = Db.DoctorReports.Single();
        row.ShareExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        Db.SaveChanges();

        (await _sut.GetPublicMetaAsync(dto.Link.Token!)).Should().BeNull();
        (await _sut.OpenPublicPdfAsync(dto.Link.Token!)).Should().BeNull();
        (await _sut.ListAsync(_me.Id)).Single().Link.Status.Should().Be(DoctorReportLinkStatus.Expired);
    }

    [Fact]
    public async Task OtherUser_CannotSeeShareRevokeDeleteOrDownload()
    {
        var dto = await CreateAsync(shareDays: 14);
        var stranger = Db.AddUser();

        (await _sut.ListAsync(stranger.Id)).Should().BeEmpty();
        (await _sut.ShareAsync(stranger.Id, dto.Id, 7)).Result.Should().Be(DoctorReportResult.NotFound);
        (await _sut.RevokeAsync(stranger.Id, dto.Id)).Result.Should().Be(DoctorReportResult.NotFound);
        (await _sut.DeleteAsync(stranger.Id, dto.Id)).Should().Be(DoctorReportResult.NotFound);
        (await _sut.OpenOwnerPdfAsync(stranger.Id, dto.Id)).Should().BeNull();
        Db.DoctorReports.Should().ContainSingle();
    }

    [Fact]
    public async Task Owner_CanDownloadPdf_WithDatedFileName()
    {
        var dto = await CreateAsync();

        var pdf = await _sut.OpenOwnerPdfAsync(_me.Id, dto.Id);

        pdf!.Value.FileName.Should().StartWith("otchet-dlya-vracha-").And.EndWith(".pdf");
        await using var content = pdf.Value.Content;
        using var bytes = new MemoryStream();
        await content.CopyToAsync(bytes);
        Encoding.ASCII.GetString(bytes.ToArray(), 0, 4).Should().Be("%PDF");
        Row(dto.Id).ShareViewCount.Should().Be(0, "скачивание владельцем — не открытие врачом");
    }

    [Fact]
    public async Task Delete_RemovesRowAttachmentAndBlob_AndKillsTheLink()
    {
        var dto = await CreateAsync(shareDays: 14);

        (await _sut.DeleteAsync(_me.Id, dto.Id)).Should().Be(DoctorReportResult.Success);

        Db.DoctorReports.Should().BeEmpty();
        Db.FileAttachments.Should().BeEmpty();
        _blobs.Should().BeEmpty();
        (await _sut.GetPublicMetaAsync(dto.Link.Token!)).Should().BeNull();
    }

    [Fact]
    public async Task List_IsOwnOnly_NewestFirst()
    {
        var older = await CreateAsync();
        await Task.Delay(15);
        var newer = await CreateAsync();
        var stranger = Db.AddUser();
        stranger.LastName = "Чужой";
        stranger.FirstName = "Иван";
        Db.SaveChanges();
        await CreateAsync(owner: stranger);

        var list = await _sut.ListAsync(_me.Id);

        list.Select(r => r.Id).Should().Equal(newer.Id, older.Id);
    }

    [Fact]
    public async Task Preview_ReturnsCounts_AndValidatesPeriod()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var ok = await _sut.PreviewAsync(_me.Id, today.AddMonths(-1), today);
        var bad = await _sut.PreviewAsync(_me.Id, today, today.AddDays(-3));

        ok.Result.Should().Be(DoctorReportResult.Success);
        ok.Counts.Should().Be(new ReportCounts(0, 0, 0, 0));
        bad.Result.Should().Be(DoctorReportResult.Invalid);
    }
}
