using FamilyHub.Infrastructure.Enrichment;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FamilyHub.UnitTests.TestSupport;

/// <summary>No-op WebSearchCallLogger для тестов провайдеров (Brave/Yandex), которым не важен сам
/// аудит-лог — CreateScope() на несконфигурированном NSubstitute-заглушке возвращает null,
/// LogAsync ловит это как любое другое исключение (см. class doc WebSearchCallLogger — аудит
/// никогда не должен ронять сам поиск) и просто логирует предупреждение, тест не падает.</summary>
public static class TestWebSearchCallLogger
{
    public static WebSearchCallLogger NoOp() =>
        new(Substitute.For<IServiceScopeFactory>(), NullLogger<WebSearchCallLogger>.Instance);
}
