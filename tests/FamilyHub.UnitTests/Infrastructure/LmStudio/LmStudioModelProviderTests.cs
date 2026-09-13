using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
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

    // --- Уровень "размышлений" (§1 плана "живой поток мыслей") — тот же приём/те же гарантии,
    // что у GetActiveModelAsync выше, отдельным кэш-ключом. ---

    [Fact]
    public async Task GetActiveReasoningAsync_NoRow_ReturnsFallback()
    {
        var result = await _sut.GetActiveReasoningAsync(LmStudioReasoning.None);

        result.Should().Be(LmStudioReasoning.None);
    }

    [Fact]
    public async Task GetActiveReasoningAsync_RowExists_ReturnsConfiguredReasoning_NotFallback()
    {
        Db.LmStudioReasoningConfigs.Add(new LmStudioReasoningConfig
        {
            Id = Guid.NewGuid(), Reasoning = LmStudioReasoning.Maximum, UpdatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var result = await _sut.GetActiveReasoningAsync(LmStudioReasoning.None);

        result.Should().Be(LmStudioReasoning.Maximum);
    }

    /// <summary>Регрессия для дизайн-заметки в LmStudioModelProvider.GetActiveReasoningAsync —
    /// закэшированное None (реально сохранённое значение) не должно путаться с "нет строки вовсе"
    /// (default(LmStudioReasoning) тоже None) и не должно быть перезаписано фолбэком.</summary>
    [Fact]
    public async Task GetActiveReasoningAsync_ConfiguredAsNone_DoesNotFallBackOnRefetch()
    {
        Db.LmStudioReasoningConfigs.Add(new LmStudioReasoningConfig
        {
            Id = Guid.NewGuid(), Reasoning = LmStudioReasoning.None, UpdatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        (await _sut.GetActiveReasoningAsync(LmStudioReasoning.Maximum)).Should().Be(LmStudioReasoning.None);
        // Второй вызов — из кэша, не из БД; тот же результат подтверждает, что кэш хранит
        // "реально None", а не "нет значения, кэш пуст".
        (await _sut.GetActiveReasoningAsync(LmStudioReasoning.Maximum)).Should().Be(LmStudioReasoning.None);
    }

    [Fact]
    public async Task GetActiveReasoningAsync_CachesResult_DoesNotSeeChangeUntilInvalidated()
    {
        Db.LmStudioReasoningConfigs.Add(new LmStudioReasoningConfig
        {
            Id = Guid.NewGuid(), Reasoning = LmStudioReasoning.Minimal, UpdatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();
        (await _sut.GetActiveReasoningAsync(LmStudioReasoning.None)).Should().Be(LmStudioReasoning.Minimal);

        await using (var db2 = NewContext())
        {
            db2.LmStudioReasoningConfigs.Single().Reasoning = LmStudioReasoning.Maximum;
            await db2.SaveChangesAsync();
        }

        (await _sut.GetActiveReasoningAsync(LmStudioReasoning.None)).Should().Be(
            LmStudioReasoning.Minimal, "кэш ещё не инвалидирован");

        _sut.InvalidateReasoning();

        (await _sut.GetActiveReasoningAsync(LmStudioReasoning.None)).Should().Be(LmStudioReasoning.Maximum);
    }
}
