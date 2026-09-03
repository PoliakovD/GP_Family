namespace FamilyHub.Infrastructure.Prompts;

/// <summary>Резолвит версионируемый текстовый шаблон по ключу — не только LLM-системные промпты
/// (Modules.Medical.Extraction/Enrichment), но и шаблоны поисковых запросов во внешний поиск
/// (AnalyteSearchQueryBuilder, BraveSearchProvider/YandexSearchProvider) — оба рода потребителей
/// живут в разных проектах (Modules.Medical/Infrastructure), поэтому сам провайдер — в
/// Infrastructure (нижний общий слой), а не в Modules.Medical, где он был до пересборки
/// enrich-пайплайна (§2 плана), пока правкой промптов не понадобилось управлять и запросами поиска.
///
/// Интерфейс, не только конкретный класс — unit-тесты потребителей (OcrNameCorrector,
/// SpecimenResolver и т.п.) подставляют сюда NSubstitute-заглушку, возвращающую fallback как
/// есть, вместо поднятия настоящего AppDbContext ради значения, которое в этих тестах всегда
/// фолбэк (активных версий в БД нет).</summary>
public interface IPromptProvider
{
    Task<string> GetAsync(string key, string fallback, CancellationToken ct = default);

    void Invalidate(string key);
}
