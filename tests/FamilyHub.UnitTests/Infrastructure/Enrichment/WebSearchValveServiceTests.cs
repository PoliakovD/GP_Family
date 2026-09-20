using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Enrichment;

/// <summary>Вентиль платного веб-поиска (замена месячной квоты, ADR-0005 §9) — единственная
/// строка WebSearchConfig, отсутствие строки означает "открыт". Намеренно БЕЗ кеша (в отличие от
/// PipelineConfigService/LmStudioModelProvider) — тесты здесь в первую очередь регрессионный гард
/// на случай, если кто-то потом добавит IMemoryCache: закрытие вентиля обязано быть видно
/// СЛЕДУЮЩЕМУ платному вызову немедленно, а не после истечения TTL.</summary>
public class WebSearchValveServiceTests : SqliteTestBase
{
    [Fact]
    public async Task IsPausedAsync_NoRow_ReturnsFalse()
    {
        var service = new WebSearchValveService(Db);

        (await service.IsPausedAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetPausedAsync_True_CreatesRowWithPausedAt()
    {
        var service = new WebSearchValveService(Db);

        await service.SetPausedAsync(true, "экономим грант");

        (await service.IsPausedAsync()).Should().BeTrue();
        var row = await NewContext().WebSearchConfigs.SingleAsync();
        row.IsPaused.Should().BeTrue();
        row.PausedAt.Should().NotBeNull();
        row.Note.Should().Be("экономим грант");
    }

    [Fact]
    public async Task SetPausedAsync_Twice_UpdatesSameRow_NotCreatesSecond()
    {
        var service = new WebSearchValveService(Db);

        await service.SetPausedAsync(true, "первая причина");
        await service.SetPausedAsync(false, null);

        var rows = await NewContext().WebSearchConfigs.ToListAsync();
        rows.Should().ContainSingle();
        rows[0].IsPaused.Should().BeFalse();
        rows[0].PausedAt.Should().BeNull();
        rows[0].Note.Should().BeNull();
    }

    [Fact]
    public async Task IsPausedAsync_NoStaleRead_SeesChangeMadeThroughAnotherContext()
    {
        // Регрессионный гард: если кто-то когда-нибудь обернёт этот сервис кешем (тот же приём,
        // что PipelineConfigService/LmStudioModelProvider), этот тест должен упасть — изменение,
        // сделанное МИМО данного экземпляра сервиса (другой DbContext, как в реальности —
        // отдельный HTTP-запрос PUT /web-search и отдельный запрос процессора), обязано быть
        // видно немедленно, без TTL-задержки.
        var service = new WebSearchValveService(Db);
        (await service.IsPausedAsync()).Should().BeFalse();

        await using var otherContext = NewContext();
        await new WebSearchValveService(otherContext).SetPausedAsync(true, null);

        (await service.IsPausedAsync()).Should().BeTrue();
    }
}
