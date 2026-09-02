using FamilyHub.Infrastructure.Search;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Search;

/// <summary>
/// Пересборка enrich-пайплайна: <see cref="LabAnalyteNameCleaner.Clean"/> даёт текст ДЛЯ ЧЕЛОВЕКА
/// (сохраняет скобки/единицы/аббревиатуры), в отличие от <see cref="LabAnalyteNormalizer.Normalize"/>,
/// который даёт ключ дедупликации — см. <c>LabAnalyteNormalizerTests</c> для того же набора кейсов
/// на уровне ключа.
/// </summary>
public class LabAnalyteNameCleanerTests
{
    [Theory]
    [InlineData("1. Гемоглобин", "Гемоглобин")]
    [InlineData("12) Лейкоциты", "Лейкоциты")]
    [InlineData("1.2 Белок", "Белок")]
    [InlineData("5 Гемоглобин", "Гемоглобин")]
    public void Clean_StripsLeadingNumbering(string raw, string expected)
    {
        LabAnalyteNameCleaner.Clean(raw).Should().Be(expected);
    }

    [Fact]
    public void Clean_StripsEchoIndexFromCorrectorResponse()
    {
        // OcrNameCorrector подписывает элементы списка "[N] Имя" перед отправкой модели — если
        // модель эхом вернула подпись вместе с исправлением, она не должна попасть в DisplayName.
        LabAnalyteNameCleaner.Clean("[0] Гемоглобин").Should().Be("Гемоглобин");
    }

    [Fact]
    public void Clean_AllCaps_LowercasesButKeepsAbbreviationsAndUnits()
    {
        LabAnalyteNameCleaner.Clean("ГЕМОГЛОБИН (HGB), Г/Л").Should().Be("Гемоглобин (HGB), Г/Л");
    }

    [Theory]
    [InlineData("СОЭ")]
    [InlineData("АЛТ")]
    [InlineData("ЛПНП")]
    public void Clean_AllCaps_PreservesShortAbbreviations(string abbreviation)
    {
        LabAnalyteNameCleaner.Clean(abbreviation).Should().Be(abbreviation);
    }

    [Theory]
    [InlineData("ВИТАМИН B12", "Витамин B12")]
    [InlineData("17-ОН-ПРОГЕСТЕРОН", "17-ОН-прогестерон")]
    public void Clean_AllCaps_PreservesTokensWithDigitsOrLatin(string raw, string expected)
    {
        LabAnalyteNameCleaner.Clean(raw).Should().Be(expected);
    }

    [Fact]
    public void Clean_NormalCasing_IsNotTouched()
    {
        // Уже нормальный регистр (есть строчные буквы) не трогаем — только однозначный сплошной
        // КАПС считается артефактом распознавания.
        LabAnalyteNameCleaner.Clean("pH мочи").Should().Be("pH мочи");
    }

    [Fact]
    public void Clean_MixedScriptHomoglyphs_Fixed()
    {
        LabAnalyteNameCleaner.Clean("Гемoглoбин").Should().Be("Гемоглобин");
    }

    [Fact]
    public void Clean_TrailingPunctuation_Trimmed()
    {
        LabAnalyteNameCleaner.Clean("Гемоглобин,").Should().Be("Гемоглобин");
        LabAnalyteNameCleaner.Clean("Гемоглобин -").Should().Be("Гемоглобин");
    }

    [Fact]
    public void Clean_KeepsParenthesesAndUnits_UnlikeNormalize()
    {
        LabAnalyteNameCleaner.Clean("Гемоглобин (HGB), г/л").Should().Be("Гемоглобин (HGB), г/л");
    }

    [Fact]
    public void Clean_EmptyOrWhitespace_ReturnsEmpty()
    {
        LabAnalyteNameCleaner.Clean("   ").Should().BeEmpty();
        LabAnalyteNameCleaner.Clean(null).Should().BeEmpty();
    }
}
