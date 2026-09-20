using FamilyHub.Infrastructure.Search;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Search;

/// <summary>
/// Ветка medicalrecords: ключ дедупликации показателя (LabIndicator.AnalyteKey /
/// GlobalLabAnalyteKb.NormalizedName) должен быть устойчив к сокращению в скобках, единицам
/// измерения и артефактам OCR (латинские гомоглифы) — тот же принцип, что у
/// MedicationNameNormalizer, отдельный словарь под показатели анализов.
/// </summary>
public class LabAnalyteNormalizerTests
{
    [Theory]
    [InlineData("Гемоглобин (HGB), г/л", "гемоглобин")]
    [InlineData("Глюкоза, ммоль/л", "глюкоза")]
    [InlineData("Лейкоциты (WBC)", "лейкоциты")]
    [InlineData("Эритроциты, ×10^12/л", "эритроциты")]
    public void Normalize_StripsParentheticalsAndUnits(string raw, string expected)
    {
        LabAnalyteNormalizer.Normalize(raw).Should().Be(expected);
    }

    [Fact]
    public void Normalize_SubjectWithGenericLabelInParentheses_KeysOnSubjectOnly()
    {
        // Ключевой регрессионный тест уточнения родовых названий (AnalyteSubjectResolver,
        // AnalyteKeyDisambiguator) — вся схема держится на том, что скобки вырезаются ЦЕЛИКОМ,
        // а не только код/аббревиатура внутри них. Пять файлов посева на разных микроорганизмов
        // с одинаковой родовой фразой на бланке ("Бактериальные микроорганизмы") получают разные
        // AnalyteKey именно потому, что конкретный объект становится ОСНОВНЫМ именем, а родовая
        // фраза уходит в скобки — если этот тест падает, схема развода коллизий сломана.
        LabAnalyteNormalizer.Normalize("Сальмонеллы (Бактериальные микроорганизмы)").Should().Be("сальмонеллы");
        LabAnalyteNormalizer.Normalize("Стафилококк (Бактериальные микроорганизмы)").Should().Be("стафилококк");
    }

    [Fact]
    public void Normalize_EmptyOrWhitespace_ReturnsEmpty()
    {
        LabAnalyteNormalizer.Normalize("   ").Should().BeEmpty();
        LabAnalyteNormalizer.Normalize(null).Should().BeEmpty();
    }

    [Fact]
    public void Normalize_MixedLatinCyrillicHomoglyphs_FixesWithinCyrillicWord()
    {
        // 'A' и 'O' латиницей внутри кириллического слова — типичный артефакт OCR.
        LabAnalyteNormalizer.Normalize("Гемoглoбин").Should().Be("гемоглобин");
    }

    [Fact]
    public void Normalize_PureLatinWord_NotMangled()
    {
        LabAnalyteNormalizer.Normalize("PSA общий").Should().Be("psa общий");
    }

    [Fact]
    public void Normalize_IsCaseInsensitiveAndTrims()
    {
        LabAnalyteNormalizer.Normalize("  ГЕМОГЛОБИН  ").Should().Be("гемоглобин");
    }

    [Theory]
    [InlineData("1. Гемоглобин", "гемоглобин")]
    [InlineData("12) Лейкоциты", "лейкоциты")]
    [InlineData("1.2 Белок", "белок")]
    [InlineData("5 Гемоглобин", "гемоглобин")]
    [InlineData("[0] Гемоглобин", "гемоглобин")]
    public void Normalize_StripsLeadingNumberingAndEchoIndex(string raw, string expected)
    {
        // Пересборка enrich-пайплайна: нумерация пункта бланка ("1. ") и эхо-подпись, которую
        // модель иногда возвращает вместе с исправленным текстом ("[0] "), не должны попадать в
        // ключ дедупликации — иначе один и тот же показатель на разных бланках расходится на
        // разные строки справочника.
        LabAnalyteNormalizer.Normalize(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("витамин B12", "витамин b12")]
    [InlineData("омега 3", "омега 3")]
    [InlineData("17-ОН-прогестерон", "17 он прогестерон")]
    public void Normalize_KeepsDigitsThatAreNotLeadingNumbering(string raw, string expected)
    {
        LabAnalyteNormalizer.Normalize(raw).Should().Be(expected);
    }

    /// <summary>
    /// Миграция AnalyteKey (план "миграция AnalyteKey", продолжение "кросс-алфавитного
    /// сопоставления показателей") — NormalizeAnalyteKey() ДОЛЖЕН давать РАВНЫЙ ключ для латинского
    /// и кириллического написания одного понятия, а не просто высокую схожесть (как транзитный
    /// Fold в вето). Именно эта пара в проде расходилась по разным AnalyteKey/NormalizedName до
    /// миграции: оба варианта — одна и та же (несклоняемая) форма "аденовирус" на обоих языках.
    /// </summary>
    [Fact]
    public void NormalizeAnalyteKey_ProductionLogPair_NominativeForm_ProducesIdenticalKey()
    {
        LabAnalyteNormalizer.NormalizeAnalyteKey("Антиген Adenovirus (B,C,E)")
            .Should().Be(LabAnalyteNormalizer.NormalizeAnalyteKey("Антиген аденовирус (B, C, E)"));
    }

    /// <summary>
    /// Честная граница миграции: точное равенство ключей достижимо только там, где словарный
    /// перевод и естественная русская формулировка совпадают по ГРАММАТИЧЕСКОЙ ФОРМЕ. Словарь даёт
    /// именительный падеж пословно ("hepatitis"→"гепатит", "virus"→"вирус"), а естественная русская
    /// фраза склоняет их в родительном ("антиген ВИРУСА ГЕПАТИТА B" — "поверхностный антиген"
    /// буквально), поэтому ключи остаются РАЗНЫМИ строками даже после свёртки. Не пытаемся
    /// это закрывать стеммингом — у RussianStemmer как раз для этого слова известный баг (см. doc
    /// MedicalTextTransliterator.IsTranslationOf: "гепатит"→"гепат", "гепатита"→"гепатит", РАЗНЫЕ
    /// основы) — стемминг сделал бы только хуже. Эта пара по-прежнему выигрывает от свёртки: обе
    /// формы остаются похожими (см. MedicalTextTransliteratorTests) и одинаково находят статью
    /// kb.global_lab_analytes_kb через нечёткий Postgres-поиск (LabAnalyteKbLookupService,
    /// AutoLinkConfidence 0.55) — просто НЕ делят один <c>AnalyteKey</c>/график тренда.
    /// </summary>
    [Fact]
    public void NormalizeAnalyteKey_ProductionLogPair_GrammaticalCaseMismatch_KeysStayDifferent()
    {
        LabAnalyteNormalizer.NormalizeAnalyteKey("Антиген Hepatitis B virus surface")
            .Should().NotBe(LabAnalyteNormalizer.NormalizeAnalyteKey("Антиген гепатита В вируса поверхность"));
    }

    [Fact]
    public void NormalizeAnalyteKey_DifferentConcepts_ProducesDifferentKeys()
    {
        // Регрессия для самой свёртки — не должна размывать реально разные показатели/препараты
        // в один ключ только потому, что оба прогнаны через Fold.
        LabAnalyteNormalizer.NormalizeAnalyteKey("Ибупрофен").Should().NotBe(LabAnalyteNormalizer.NormalizeAnalyteKey("Парацетамол"));
    }

    [Fact]
    public void NormalizeAnalyteKey_VocabularyOutsideDictionary_DoesNotMerge()
    {
        // Честная граница: побуквенная транслитерация "Glucose" даёт "глукосе", а не "глюкоза" —
        // NormalizeAnalyteKey не универсальный решатель, а точечное расширение словаря по мере
        // появления новых расхождений в логах (см. class doc MedicalTextTransliterator). Тест
        // фиксирует границу явно, чтобы будущий читатель не считал её забытым багом.
        LabAnalyteNormalizer.NormalizeAnalyteKey("Glucose").Should().NotBe(LabAnalyteNormalizer.NormalizeAnalyteKey("Глюкоза"));
    }

    [Fact]
    public void NormalizeAnalyteKey_PureCyrillicInput_SameAsNormalize()
    {
        // Никакой латиницы — свёртка не должна ничего менять сверх обычного Normalize.
        LabAnalyteNormalizer.NormalizeAnalyteKey("Гемоглобин (HGB), г/л").Should().Be("гемоглобин");
    }
}
