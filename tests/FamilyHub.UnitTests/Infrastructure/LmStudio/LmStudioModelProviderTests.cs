using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.LmStudio;

/// <summary>Резолвинг активной модели LM Studio из админки — тот же приём и та же гарантия, что
/// PromptProviderTests: строка в БД побеждает фолбэк из кода, кэш инвалидируется явно при записи
/// (см. class doc LmStudioModelProvider).</summary>
public class LmStudioModelProviderTests : SqliteTestBase
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly LmStudioModelProvider _sut;

    public LmStudioModelProviderTests()
    {
        _sut = new LmStudioModelProvider(Db, _cache);
    }

    [Fact]
    public async Task GetActiveModelAsync_NoRow_ReturnsFallback()
    {
        var result = await _sut.GetActiveModelAsync("prism-ml/bonsai-27b");

        result.Should().Be("prism-ml/bonsai-27b");
    }

    [Fact]
    public async Task GetActiveModelAsync_RowExists_ReturnsConfiguredModel_NotFallback()
    {
        Db.LmStudioModelConfigs.Add(new LmStudioModelConfig
        {
            Id = Guid.NewGuid(), ModelId = "qwen3.5-9b-uncensored", UpdatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var result = await _sut.GetActiveModelAsync("prism-ml/bonsai-27b");

        result.Should().Be("qwen3.5-9b-uncensored");
    }

    [Fact]
    public async Task GetActiveModelAsync_CachesResult_DoesNotSeeChangeUntilInvalidated()
    {
        Db.LmStudioModelConfigs.Add(new LmStudioModelConfig
        {
            Id = Guid.NewGuid(), ModelId = "model-v1", UpdatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();
        (await _sut.GetActiveModelAsync("фолбэк")).Should().Be("model-v1");

        // Меняем строку напрямую в БД, минуя LmStudioModelProvider.Invalidate — кэш ещё не знает.
        await using (var db2 = NewContext())
        {
            db2.LmStudioModelConfigs.Single().ModelId = "model-v2";
            await db2.SaveChangesAsync();
        }

        (await _sut.GetActiveModelAsync("фолбэк")).Should().Be("model-v1", "кэш ещё не инвалидирован");

        _sut.Invalidate();

        (await _sut.GetActiveModelAsync("фолбэк")).Should().Be("model-v2");
    }
}
