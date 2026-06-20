using System.Text;
using FamilyHub.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Storage;

public class LocalFileStorageTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "fh-tests-" + Guid.NewGuid());
    private readonly LocalFileStorage _sut;

    public LocalFileStorageTests()
    {
        _sut = new LocalFileStorage(Options.Create(new LocalFileStorageOptions
        {
            RootPath = _rootPath,
            PublicBasePath = "/local-files",
            SigningKey = "test-signing-key",
        }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath)) Directory.Delete(_rootPath, recursive: true);
    }

    [Fact]
    public async Task SaveAsync_WritesFileToDiskUnderRootPath()
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        await _sut.SaveAsync("medical-records/abc/scan.pdf", content, content.Length, "application/pdf");

        var path = _sut.ResolvePath("medical-records/abc/scan.pdf");
        File.Exists(path).Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Be("hello");
    }

    [Fact]
    public async Task GetPresignedUrlAsync_ProducesUrlWithSignatureThatValidates()
    {
        var url = await _sut.GetPresignedUrlAsync("medical-records/abc/scan.pdf", TimeSpan.FromMinutes(5));

        url.Should().StartWith("/local-files/medical-records/abc/scan.pdf?expires=");
        var (storageKey, expires, sig) = ParseUrl(url);

        _sut.IsValidSignature(storageKey, expires, sig).Should().BeTrue();
    }

    [Fact]
    public async Task IsValidSignature_TamperedSignature_ReturnsFalse()
    {
        var url = await _sut.GetPresignedUrlAsync("medical-records/abc/scan.pdf", TimeSpan.FromMinutes(5));
        var (storageKey, expires, sig) = ParseUrl(url);

        _sut.IsValidSignature(storageKey, expires, sig + "0").Should().BeFalse();
    }

    [Fact]
    public async Task IsValidSignature_DifferentStorageKeyThanSigned_ReturnsFalse()
    {
        var url = await _sut.GetPresignedUrlAsync("medical-records/abc/scan.pdf", TimeSpan.FromMinutes(5));
        var (_, expires, sig) = ParseUrl(url);

        _sut.IsValidSignature("medical-records/other/scan.pdf", expires, sig).Should().BeFalse();
    }

    [Fact]
    public async Task IsValidSignature_ExpiredLink_ReturnsFalse()
    {
        var url = await _sut.GetPresignedUrlAsync("medical-records/abc/scan.pdf", TimeSpan.FromSeconds(-1));
        var (storageKey, expires, sig) = ParseUrl(url);

        _sut.IsValidSignature(storageKey, expires, sig).Should().BeFalse();
    }

    private static (string StorageKey, long Expires, string Signature) ParseUrl(string url)
    {
        // url: /local-files/{storageKey}?expires={unix}&sig={hex}
        var withoutPrefix = url["/local-files/".Length..];
        var queryIndex = withoutPrefix.IndexOf('?');
        var storageKey = withoutPrefix[..queryIndex];
        var query = System.Web.HttpUtility.ParseQueryString(withoutPrefix[(queryIndex + 1)..]);
        return (storageKey, long.Parse(query["expires"]!), query["sig"]!);
    }
}
