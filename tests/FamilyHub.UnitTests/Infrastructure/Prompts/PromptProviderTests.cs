using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Prompts;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Prompts;

/// <summary>Резолвинг текста промпта по ключу (управление enrich-пайплайном из админки, §2 плана) —
/// активная версия из БД побеждает фолбэк на константу в коде, кэш инвалидируется явно при
/// создании/активации новой версии (см. class doc PromptProvider).</summary>
public class PromptProviderTests : SqliteTestBase
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly PromptProvider _sut;

    public PromptProviderTests()
    {
        _sut = new PromptProvider(Db, _cache);
    }

    [Fact]
    public async Task GetAsync_NoActiveVersion_ReturnsFallback()
    {
        var result = await _sut.GetAsync("analysis.extract", "код-фолбэк");

        result.Should().Be("код-фолбэк");
    }

    [Fact]
    public async Task GetAsync_ActiveVersionExists_ReturnsItsBody_NotFallback()
    {
        await SeedPromptAsync("analysis.extract", "текст из БД", isActive: true);

        var result = await _sut.GetAsync("analysis.extract", "код-фолбэк");

        result.Should().Be("текст из БД");
    }

    [Fact]
    public async Task GetAsync_InactiveVersionOnly_ReturnsFallback()
    {
        await SeedPromptAsync("analysis.extract", "старый неактивный текст", isActive: false);

        var result = await _sut.GetAsync("analysis.extract", "код-фолбэк");

        result.Should().Be("код-фолбэк");
    }

    [Fact]
    public async Task GetAsync_CachesResult_DoesNotSeeChangeUntilInvalidated()
    {
        await SeedPromptAsync("analysis.extract", "версия 1", isActive: true);
        (await _sut.GetAsync("analysis.extract", "фолбэк")).Should().Be("версия 1");

        // Активируем новую версию напрямую в БД, минуя PromptProvider.Invalidate — кэш ещё не знает.
        await using (var db2 = NewContext())
        {
            var prompt = db2.PipelinePrompts.Single(p => p.Key == "analysis.extract");
            db2.PipelinePromptVersions.Single(v => v.PromptId == prompt.Id && v.IsActive).IsActive = false;
            db2.PipelinePromptVersions.Add(new PipelinePromptVersion
            {
                Id = Guid.NewGuid(), PromptId = prompt.Id, Version = 2, Body = "версия 2", IsActive = true, CreatedAt = DateTime.UtcNow,
            });
            await db2.SaveChangesAsync();
        }

        (await _sut.GetAsync("analysis.extract", "фолбэк")).Should().Be("версия 1", "кэш ещё не инвалидирован");

        _sut.Invalidate("analysis.extract");

        (await _sut.GetAsync("analysis.extract", "фолбэк")).Should().Be("версия 2");
    }

    private async Task SeedPromptAsync(string key, string body, bool isActive)
    {
        var prompt = new PipelinePrompt { Id = Guid.NewGuid(), Key = key, Description = "тест", CreatedAt = DateTime.UtcNow };
        Db.PipelinePrompts.Add(prompt);
        Db.PipelinePromptVersions.Add(new PipelinePromptVersion
        {
            Id = Guid.NewGuid(), PromptId = prompt.Id, Version = 1, Body = body, IsActive = isActive, CreatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();
    }
}
