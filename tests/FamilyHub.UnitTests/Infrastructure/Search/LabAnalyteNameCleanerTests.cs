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

    [Fact]
    public void CleanPersonName_AllCaps_CapitalizesEveryWord_UnlikeClean()
    {
        // Отличие от Clean (капитализирует только первое слово фразы, годится для терминов вроде
        // "Общий белок") — ФИО состоит из нескольких имён собственных, каждое капитализируется
        // отдельно (пересборка enrich-пайплайна, §5 плана: строка врача в записи).
        LabAnalyteNameCleaner.CleanPersonName("ИВАНОВ ИВАН ИВАНОВИЧ").Should().Be("Иванов Иван Иванович");
    }

    [Fact]
    public void CleanPersonName_StripsLeadingNumbering()
    {
        LabAnalyteNameCleaner.CleanPersonName("1. Иванов И.И.").Should().Be("Иванов И.И.");
    }

    [Fact]
    public void CleanPersonName_NormalCasing_IsNotTouched()
    {
        LabAnalyteNameCleaner.CleanPersonName("Иванов Иван Иванович").Should().Be("Иванов Иван Иванович");
    }

    [Fact]
    public void CleanPersonName_EmptyOrWhitespace_ReturnsEmpty()
    {
        LabAnalyteNameCleaner.CleanPersonName("   ").Should().BeEmpty();
        LabAnalyteNameCleaner.CleanPersonName(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("MCV (ср. объем эритр.) 84.2", "84.2", "MCV (ср. объем эритр.)")]
    [InlineData("Гематокрит 50.0*", "50.0", "Гематокрит")]
    [InlineData("Гемоглобин", "17.3", "Гемоглобин")]
    [InlineData("Лейкоциты 7.14", "", "Лейкоциты 7.14")]
    [InlineData("Базофилы, % 0.3", "0.3", "Базофилы, %")]
    [InlineData("Ген 5", "5", "Ген")]
    [InlineData("Витамин B12", "12", "Витамин B12")] // цифры — часть названия, не слипшееся значение
    public void BlankNameWithoutValue_CutsGluedValueOnlyWhenCellEndsWithIt(string cell, string value, string expected)
    {
        LabAnalyteNameCleaner.BlankNameWithoutValue(cell, value).Should().Be(expected);
    }

    [Theory]
    [InlineData("MCV", "MCV (ср. объем эритр.)", "MCV (ср. объем эритр.)")]
    [InlineData("MCH", "MCH (ср. содер. Hb в эр.)", "MCH (ср. содер. Hb в эр.)")]
    [InlineData("Эозинофилы", "Эозинофилы, %", "Эозинофилы, %")]
    [InlineData("Нейтрофилы", "Нейтрофилы (общ.число), %", "Нейтрофилы (общ.число), %")]
    [InlineData("Гемоглобин", "1. Гемоглобин", "Гемоглобин")]
    [InlineData("МСHС", "МСHС (ср. конц. Hb в эр.)", "МСНС (ср. конц. Hb в эр.)")] // смешанный алфавит в бланке чинится Clean
    [InlineData("Гем", "Гемоглобин", "Гем")] // не граница слова — не подменяем
    [InlineData("Глюкоза", "Гемоглобин", "Глюкоза")] // другое понятие — имя модели не перетираем
    [InlineData("Гемоглобин", "Гемоглобин", "Гемоглобин")]
    public void PreferFullBlankName_TakesLongerBlankNameOnlyWhenItStartsWithModelName(string candidate, string blank, string expected)
    {
        LabAnalyteNameCleaner.PreferFullBlankName(candidate, blank).Should().Be(expected);
    }

    [Fact]
    public void PreferFullBlankName_TooLongBlankName_KeepsModelName()
    {
        var blank = "Показатель " + new string('я', 200);

        LabAnalyteNameCleaner.PreferFullBlankName("Показатель", blank, maxLength: 160).Should().Be("Показатель");
    }

    /// <summary>Прод-случай: модель вернула значение «29.8», а в ячейке бланка «29,8» (иногда с маркером отклонения) —
    /// строгое сравнение оставляло значение в названии, и «Гематокрит крови 29,8» уходил в ключ и в платный поиск.</summary>
    [Theory]
    [InlineData("Гематокрит крови 29,8", "29.8", "Гематокрит крови")]
    [InlineData("Гематокрит крови 29.8", "29,8", "Гематокрит крови")]
    [InlineData("Гематокрит крови 29,8 ↓", "29.8", "Гематокрит крови")]
    [InlineData("Гематокрит крови 29,8 L", "29.8", "Гематокрит крови")]
    [InlineData("Гематокрит крови 29,8*", "29.8", "Гематокрит крови")]
    [InlineData("Витамин B12", "12", "Витамин B12")]
    [InlineData("Гематокрит крови 29,8", "29.9", "Гематокрит крови 29,8")]
    public void BlankNameWithoutValue_DecimalSeparatorAndFlags_DoNotKeepValueInName(string cell, string value, string expected)
    {
        LabAnalyteNameCleaner.BlankNameWithoutValue(cell, value).Should().Be(expected);
    }

    [Theory]
    [InlineData("Гематокрит крови", "Гематокрит крови 29,8")]
    [InlineData("Гематокрит крови", "Гематокрит крови 29.8 ↓")]
    [InlineData("Гематокрит крови", "Гематокрит крови < 5")]
    public void PreferFullBlankName_BlankDiffersOnlyByNumber_KeepsModelName(string candidate, string blank)
    {
        LabAnalyteNameCleaner.PreferFullBlankName(candidate, blank).Should().Be(candidate);
    }
}
