using FamilyHub.Infrastructure.Prompts;
using NSubstitute;

namespace FamilyHub.UnitTests.TestSupport;

/// <summary>NSubstitute-заглушка IPromptProvider, возвращающая fallback как есть — активных версий
/// промптов в БД в этих unit-тестах нет и не будет (это поднимало бы AppDbContext ради значения,
/// которое всегда фолбэк), поэтому реальный PromptProvider тут не нужен.</summary>
public static class TestPromptProvider
{
    public static IPromptProvider ReturningFallback()
    {
        var provider = Substitute.For<IPromptProvider>();
        provider.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult((string)ci[1]));
        return provider;
    }
}
