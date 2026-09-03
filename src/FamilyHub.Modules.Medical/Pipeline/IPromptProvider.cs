namespace FamilyHub.Modules.Medical.Pipeline;

/// <summary>Интерфейс над <see cref="PromptProvider"/> — единственная причина существования:
/// unit-тесты промпт-потребителей (OcrNameCorrector, SpecimenResolver и т.п.) подставляют сюда
/// NSubstitute-заглушку, возвращающую fallback как есть, вместо поднятия настоящего AppDbContext
/// ради значения, которое в этих тестах всегда фолбэк (активных версий в БД нет).</summary>
public interface IPromptProvider
{
    Task<string> GetAsync(string key, string fallback, CancellationToken ct = default);

    void Invalidate(string key);
}
