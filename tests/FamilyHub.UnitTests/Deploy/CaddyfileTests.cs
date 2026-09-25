using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Deploy;

/// <summary>
/// Регрессия: публичный домен блокировал <c>/health/*</c> целиком (защита health-check'ов), а это ещё и
/// маршруты SPA раздела «Здоровье» (/health/records, /health/medications, ...). Обновление страницы
/// на таком пути давало 404 от Caddy вместо index.html. Блокироваться должны только сами эндпоинты.
/// </summary>
public class CaddyfileTests
{
    private static string ReadCaddyfile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "deploy", "Caddyfile")))
            dir = dir.Parent;
        dir.Should().NotBeNull("deploy/Caddyfile должен находиться в корне репозитория");
        return File.ReadAllText(Path.Combine(dir!.FullName, "deploy", "Caddyfile"));
    }

    private static string PublicBlockedMatcher() =>
        ReadCaddyfile().Split('\n')
            .Select(l => l.Trim())
            .First(l => l.StartsWith("@blocked path", StringComparison.Ordinal));

    [Fact]
    public void PublicDomain_DoesNotBlockSpaRoutesUnderHealth()
    {
        var matcher = PublicBlockedMatcher();

        matcher.Should().NotContain("/health/*", "иначе обновление /health/records отдаёт 404 вместо SPA");
        matcher.Should().NotContain("/health*");
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health/llm")]
    public void PublicDomain_StillBlocksHealthCheckEndpoints(string path)
    {
        PublicBlockedMatcher().Split(' ').Should().Contain(path);
    }
}
