using System.Web;
using FamilyHub.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Storage;

public class DownloadTokenServiceTests
{
    private static DownloadTokenService CreateSut(TimeSpan? ttl = null) =>
        new(Options.Create(new AttachmentDownloadOptions
        {
            DownloadSigningKey = "test-download-signing-key",
            UrlTtl = ttl ?? TimeSpan.FromMinutes(5),
        }));

    private static (Guid AttachmentId, long Expires, string Sig) ParseUrl(string url, Guid expectedId)
    {
        url.Should().StartWith($"/api/attachments/{expectedId}/file?expires=");
        var query = HttpUtility.ParseQueryString(url[(url.IndexOf('?') + 1)..]);
        return (expectedId, long.Parse(query["expires"]!), query["sig"]!);
    }

    [Fact]
    public void CreateUrl_ProducesTokenThatValidates()
    {
        var sut = CreateSut();
        var attachmentId = Guid.NewGuid();

        var (id, expires, sig) = ParseUrl(sut.CreateUrl(attachmentId), attachmentId);

        sut.Validate(id, expires, sig).Should().BeTrue();
    }

    [Fact]
    public void Validate_TamperedSignature_Fails()
    {
        var sut = CreateSut();
        var attachmentId = Guid.NewGuid();
        var (id, expires, sig) = ParseUrl(sut.CreateUrl(attachmentId), attachmentId);

        sut.Validate(id, expires, sig + "0").Should().BeFalse();
    }

    [Fact]
    public void Validate_DifferentAttachmentId_Fails()
    {
        var sut = CreateSut();
        var attachmentId = Guid.NewGuid();
        var (_, expires, sig) = ParseUrl(sut.CreateUrl(attachmentId), attachmentId);

        sut.Validate(Guid.NewGuid(), expires, sig).Should().BeFalse();
    }

    [Fact]
    public void Validate_ExpiredToken_Fails()
    {
        var sut = CreateSut(TimeSpan.FromSeconds(-1));
        var attachmentId = Guid.NewGuid();
        var (id, expires, sig) = ParseUrl(sut.CreateUrl(attachmentId), attachmentId);

        sut.Validate(id, expires, sig).Should().BeFalse();
    }

    [Fact]
    public void Validate_ExtendedExpiry_BreaksSignature()
    {
        // Продление срока жизни в URL без перевыпуска подписи должно отвергаться.
        var sut = CreateSut();
        var attachmentId = Guid.NewGuid();
        var (id, expires, sig) = ParseUrl(sut.CreateUrl(attachmentId), attachmentId);

        sut.Validate(id, expires + 3600, sig).Should().BeFalse();
    }
}
