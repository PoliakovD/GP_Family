using FamilyHub.Infrastructure.Search;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Search;

/// <summary>
/// Кросс-алфавитное сопоставление названий показателей (без LLM) — детерминированные вето
/// OcrNameCorrector/AnalyteSubjectResolver отклоняли заведомо верные совпадения только потому, что
/// одна сторона написана латиницей, другая — кириллицей (живые примеры из прод-логов ниже: скор
/// 0.27/0.12/0.00 на парах, которые для человека — одно и то же понятие).
/// </summary>
public class MedicalTextTransliteratorTests
{
    [Theory]
    // Прод: "Антиген Adenovirus (B,C,E)" / "Антиген аденовирус (B, C, E)" — было 0.27, ниже порога 0.3.
    [InlineData("антиген adenovirus", "антиген аденовирус")]
    // Прод: "Антиген Hepatitis B virus surface" / "Антиген гепатита В вируса поверхность" — было 0.13.
    [InlineData("антиген hepatitis b virus surface", "антиген гепатита в вируса поверхность")]
    public void Fold_ProductionLogPairs_SimilarityClearsCorrectionThreshold(string original, string candidate)
    {
        var similarity = TrigramSimilarity.Similarity(
            MedicalTextTransliterator.Fold(original), MedicalTextTransliterator.Fold(candidate));

        similarity.Should().BeGreaterThanOrEqualTo(0.3, "после свёртки латиница и кириллица одного понятия должны сходиться");
    }

    [Theory]
    [InlineData("Adenovirus", "аденовирус")]
    [InlineData("Rotavirus", "ротавирус")]
    [InlineData("Antigen", "антиген")]
    public void Fold_PhoneticTransliteration_MatchesNativeCyrillicWord(string latin, string cyrillic)
    {
        MedicalTextTransliterator.Fold(latin).Should().Be(MedicalTextTransliterator.Fold(cyrillic));
    }

    [Fact]
    public void Fold_DoubledConsonantVsSoftSign_FoldsToSameForm()
    {
        // "Salmonella" (двойное "л") транслитерируется в "салмонелла"; нативное "Сальмонелла" несёт
        // тот же звук мягким знаком вместо второй "л" — раскрытие двойных букв и снятие ь/ъ сводит
        // обе формы к одной ("салмонела"), см. class doc FoldCyrillicWord.
        MedicalTextTransliterator.Fold("Salmonella").Should().Be(MedicalTextTransliterator.Fold("Сальмонелла"));
    }

    [Theory]
    [InlineData("surface", "поверхность")]
    [InlineData("hepatitis", "гепатит")]
    public void Fold_TermDictionary_MatchesRussianEquivalent(string english, string russian)
    {
        // Фонетическая транслитерация здесь дала бы неверный результат ("хепатитис" вместо
        // "гепатит" — 'h' звучит как 'г' в этом заимствовании, не 'х") — целыми словами через
        // словарь, не по буквам.
        MedicalTextTransliterator.Fold(english).Should().Be(MedicalTextTransliterator.Fold(russian));
    }

    [Fact]
    public void Fold_SingleLatinLetterDesignator_UsesVisualHomoglyphNotPhonetic()
    {
        // "Гепатит B" на бланке — латинское B означает кириллическую В (визуально), а не звук "б":
        // фонетическая транслитерация тут была бы неверной и как раз разорвала бы этот случай.
        MedicalTextTransliterator.Fold("Гепатит B").Should().Be(MedicalTextTransliterator.Fold("Гепатит В"));
    }

    [Fact]
    public void Fold_PureCyrillicText_IsUnaffected()
    {
        MedicalTextTransliterator.Fold("Гемоглобин").Should().Be("гемоглобин");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Fold_EmptyOrWhitespaceOrNull_ReturnsEmptyString(string? raw)
    {
        MedicalTextTransliterator.Fold(raw).Should().Be(string.Empty);
    }

    [Fact]
    public void Fold_UnrelatedConcepts_StaysBelowCorrectionThreshold()
    {
        // Свёртка не должна размывать реальную подмену понятия — два разных препарата остаются
        // непохожими даже после Fold (регрессия для OcrNameCorrector.MinCorrectionSimilarity).
        var similarity = TrigramSimilarity.Similarity(
            MedicalTextTransliterator.Fold("ибупрофен"), MedicalTextTransliterator.Fold("парацетамол"));

        similarity.Should().BeLessThan(0.3);
    }

    [Theory]
    // Прод: OcrNameCorrector-пары — кандидат оказался переводом оригинала, а не правкой написания.
    [InlineData("антиген adenovirus", "антиген аденовирус")]
    [InlineData("антиген hepatitis b virus surface", "антиген гепатита в вируса поверхность")]
    public void IsTranslationOf_ProductionLogPairs_ReturnsTrue(string original, string candidate)
    {
        MedicalTextTransliterator.IsTranslationOf(original, candidate).Should().BeTrue();
    }

    [Fact]
    public void IsTranslationOf_SameConceptCaseFix_ReturnsFalse()
    {
        // Правка регистра/написания одного и того же понятия — не перевод, применять можно.
        MedicalTextTransliterator.IsTranslationOf("суматриптан", "суматриптан").Should().BeFalse();
    }

    [Fact]
    public void IsTranslationOf_CandidateStillLatin_ReturnsFalse()
    {
        // Кандидат не стал кириллическим — значит это не "перевод на другой алфавит".
        MedicalTextTransliterator.IsTranslationOf("adenovirus", "adenoviruses").Should().BeFalse();
    }

    [Fact]
    public void IsTranslationOf_OriginalHasNoLatin_ReturnsFalse()
    {
        MedicalTextTransliterator.IsTranslationOf("ибупрофен", "парацетамол").Should().BeFalse();
    }
}
