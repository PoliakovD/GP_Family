using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>
/// Запасной вариант (не LLM) разведения коллизий ключа МЕЖДУ РАЗНЫМИ файлами одного прогона
/// распознавания, когда AnalyteSubjectResolver не определил объект исследования (см. class doc
/// AnalyteKeyDisambiguator). Единственный источник истины о том, что показатель посева на пять
/// разных микроорганизмов не схлопывается в одну строку, даже когда шаг уточнения выключен.
/// </summary>
public class AnalyteKeyDisambiguatorTests
{
    private const string GenericKey = "бактериальные микроорганизмы";

    [Fact]
    public void Disambiguate_FiveFilesWithSameGenericKey_FirstStaysUnsuffixed_RestGetDistinctKeys()
    {
        var fileIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var candidates = fileIds.Select(id => new AnalyteKeyDisambiguator.Candidate(GenericKey, id)).ToList();

        var result = AnalyteKeyDisambiguator.Disambiguate(candidates);

        // Первый файл — без суффикса вообще (в словаре результата записи для него нет), вызывающий
        // код в этом случае продолжает использовать исходный BaseAnalyteKey как есть.
        result.Should().NotContainKey((GenericKey, fileIds[0]));

        // Остальные четыре — суффиксированы и различаются между собой.
        var suffixedKeys = fileIds.Skip(1)
            .Select(id => result[(GenericKey, id)].AnalyteKey)
            .ToList();

        suffixedKeys.Should().OnlyHaveUniqueItems();
        suffixedKeys.Should().HaveCount(4);
        suffixedKeys.Should().NotContain(GenericKey, "разведённый ключ обязан отличаться от базового");
    }

    [Fact]
    public void Disambiguate_SameFileGroupId_TreatedAsRepeatWithinOneBlank_NotSuffixed()
    {
        // Повтор строки ОДНОГО бланка (тот же FileGroupId) уже разрешён DeduplicateByName в
        // экстракторе — разводить его здесь не нужно, даже если вызывающий код по ошибке передаст
        // такие кандидаты дважды.
        var fileId = Guid.NewGuid();
        var candidates = new[]
        {
            new AnalyteKeyDisambiguator.Candidate(GenericKey, fileId),
            new AnalyteKeyDisambiguator.Candidate(GenericKey, fileId),
        };

        var result = AnalyteKeyDisambiguator.Disambiguate(candidates);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Disambiguate_DifferentBaseKeys_NoCollision_ReturnsEmpty()
    {
        var candidates = new[]
        {
            new AnalyteKeyDisambiguator.Candidate("гемоглобин", Guid.NewGuid()),
            new AnalyteKeyDisambiguator.Candidate("эритроциты", Guid.NewGuid()),
        };

        var result = AnalyteKeyDisambiguator.Disambiguate(candidates);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Disambiguate_SuffixedResult_NormalizesToSameFormAsRealAnalyteKeys()
    {
        // Ключ хранения — результат Disambiguate — обязан быть уже в форме, которую произвёл бы
        // LabAnalyteNormalizer.Normalize (тот же формат, что и у обычных, неразведённых ключей) —
        // иначе он не совпадёт со своей же формой при пересборке справочника (см.
        // LabAnalyteKbRebuildJob.RecalculateIndicatorsAsync, идемпотентность §6 плана).
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var candidates = new[]
        {
            new AnalyteKeyDisambiguator.Candidate(GenericKey, first),
            new AnalyteKeyDisambiguator.Candidate(GenericKey, second),
        };

        var result = AnalyteKeyDisambiguator.Disambiguate(candidates);

        var suffixedKey = result[(GenericKey, second)].AnalyteKey;
        suffixedKey.Should().Be(LabAnalyteNormalizer.Normalize(suffixedKey),
            "разведённый ключ должен быть уже в нормализованной форме — идемпотентен относительно Normalize");
    }
}
