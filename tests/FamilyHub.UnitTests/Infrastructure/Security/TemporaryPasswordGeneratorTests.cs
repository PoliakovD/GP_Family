using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Security;

public class TemporaryPasswordGeneratorTests
{
    [Fact]
    public void Generate_AlwaysSatisfiesPasswordRules()
    {
        // Генератор строит результат валидным ПО ПОСТРОЕНИЮ, а не generate-and-retry — если бы
        // он сам не проходил свою же политику, это был бы абсурд (см. TelegramBindingService,
        // единственный потребитель). Много итераций — ловим редкий баг в перемешивании.
        for (var i = 0; i < 200; i++)
            PasswordRules.IsValid(TemporaryPasswordGenerator.Generate()).Should().BeTrue();
    }

    [Fact]
    public void Generate_ExcludesAmbiguousCharacters()
    {
        for (var i = 0; i < 200; i++)
        {
            var password = TemporaryPasswordGenerator.Generate();
            foreach (var ambiguous in new[] { '0', 'O', '1', 'l', 'I' })
                password.Should().NotContain(ambiguous.ToString());
        }
    }

    [Fact]
    public void Generate_IsNotDeterministic()
    {
        TemporaryPasswordGenerator.Generate().Should().NotBe(TemporaryPasswordGenerator.Generate());
    }
}
