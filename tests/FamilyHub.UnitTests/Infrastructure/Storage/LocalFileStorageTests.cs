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
        _sut = new LocalFileStorage(Options.Create(new LocalFileStorageOptions { RootPath = _rootPath }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath)) Directory.Delete(_rootPath, recursive: true);
    }

    [Fact]
    public async Task SaveAsync_WritesFileToDiskUnderRootPath()
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        await _sut.SaveAsync("medical-records/abc/scan", content, content.Length, "application/pdf");

        var path = _sut.ResolvePath("medical-records/abc/scan");
        File.Exists(path).Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Be("hello");
    }

    [Fact]
    public async Task OpenReadAsync_ReturnsSavedContent()
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        await _sut.SaveAsync("a/b", content, content.Length, "application/octet-stream");

        await using var stream = await _sut.OpenReadAsync("a/b");
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).Should().Be("payload");
    }

    [Fact]
    public async Task OpenReadAsync_UnknownKey_Throws()
    {
        var act = async () => await _sut.OpenReadAsync("no/such/key");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile_AndIsIdempotent()
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("bye"));
        await _sut.SaveAsync("del/me", content, content.Length, "application/octet-stream");

        await _sut.DeleteAsync("del/me");
        File.Exists(_sut.ResolvePath("del/me")).Should().BeFalse();

        var act = async () => await _sut.DeleteAsync("del/me");
        await act.Should().NotThrowAsync();
    }
}
