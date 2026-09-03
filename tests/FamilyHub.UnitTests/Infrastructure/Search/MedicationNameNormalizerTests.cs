using FamilyHub.Infrastructure.Search;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Search;

/// <summary>
/// Этап 4: ключ дедупликации справочника (GlobalMedicationKb.NormalizedName) должен быть
/// устойчив к дозировке/фасовке/форме выпуска, которые различаются от упаковки к упаковке одного
/// и того же препарата, и к типичным артефактам OCR (латинские гомоглифы в кириллическом слове).
/// </summary>
public class MedicationNameNormalizerTests
{
    [Fact]
    public void Normalize_StripsDosagePackagingAndForm_LeavesBareName()
    {
        MedicationNameNormalizer.Normalize("Парацетамол 400мг таб. №20").Should().Be("парацетамол");
    }

    [Theory]
    [InlineData("Ибупрофен 200 мг")]
    [InlineData("Ибупрофен 0.2г")]
    [InlineData("Ибупрофен 200мкг")]
    [InlineData("Ибупрофен 5мл")]
    [InlineData("Ибупрофен 500 IU")]
    public void Normalize_StripsDosageUnits_RegardlessOfUnit(string raw)
    {
        MedicationNameNormalizer.Normalize(raw).Should().Be("ибупрофен");
    }

    [Theory]
    [InlineData("Нурофен №20")]
    [InlineData("Нурофен N20")]
    [InlineData("Нурофен n 20")]
    public void Normalize_StripsPackagingNumber(string raw)
    {
        MedicationNameNormalizer.Normalize(raw).Should().Be("нурофен");
    }

    [Theory]
    [InlineData("Аспирин таблетки")]
    [InlineData("Аспирин таблетки, покрытые оболочкой")]
    [InlineData("Аспирин капли")]
    [InlineData("Аспирин раствор")]
    [InlineData("Аспирин пролонгированный")]
    public void Normalize_StripsFormWords(string raw)
    {
        MedicationNameNormalizer.Normalize(raw).Should().Be("аспирин");
    }

    [Fact]
    public void Normalize_YoIsTreatedAsYe()
    {
        // Тот же приём, что и в RussianTextSearcher/pg_trgm-конфиге: "ё" и "е" — одна и та же
        // буква для целей поиска/дедупликации, OCR и ручной ввод не всегда их различают.
        MedicationNameNormalizer.Normalize("Мёд").Should().Be(MedicationNameNormalizer.Normalize("Мед"));
    }

    [Fact]
    public void Normalize_MixedCyrillicLatinWord_FixesHomoglyphs()
    {
        // "Аспирин", где OCR распознал часть букв ("А", "с", "р") латиницей — визуально идентичные
        // глифы (A/А, c/с, p/р), а "п", "и", "н" остались кириллицей: типичный артефакт OCR.
        MedicationNameNormalizer.Normalize("Acпиpин").Should().Be("аспирин");
    }

    [Fact]
    public void Normalize_PureLatinTradeName_IsNotMangled()
    {
        // Честное латинское название не должно ломаться попыткой починить "гомоглифы" — там их нет,
        // весь токен целиком латиница, ни одной кириллической буквы для триггера подмены.
        MedicationNameNormalizer.Normalize("Nurofen").Should().Be("nurofen");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_EmptyOrWhitespaceOrNull_ReturnsEmptyString(string? raw)
    {
        MedicationNameNormalizer.Normalize(raw).Should().Be(string.Empty);
    }

    [Fact]
    public void Normalize_CollapsesPunctuationAndWhitespace()
    {
        MedicationNameNormalizer.Normalize("  Но-шпа,   форте!  ").Should().Be("но шпа форте");
    }

    [Fact]
    public void Normalize_IsCaseInsensitive()
    {
        MedicationNameNormalizer.Normalize("ПАРАЦЕТАМОЛ").Should().Be(MedicationNameNormalizer.Normalize("парацетамол"));
    }

    [Theory]
    [InlineData("1. Парацетамол")]
    [InlineData("12) Парацетамол")]
    [InlineData("[0] Парацетамол")]
    public void Normalize_StripsLeadingNumberingOrEchoIndex_SameKeyAsBareName(string raw)
    {
        // Та же дыра, что была у LabAnalyteNormalizer до пересборки enrich-пайплайна (§5 плана) —
        // список назначенных препаратов в заключении врача тоже подписывается "[N]"
        // (OcrNameCorrector.BuildUserText), а нумерация пункта могла попасть из бланка так же, как
        // у показателей анализа. Без снятия маркера "1. Парацетамол" получал бы отдельный ключ
        // дедупликации от "Парацетамол".
        MedicationNameNormalizer.Normalize(raw).Should().Be("парацетамол");
    }
}
