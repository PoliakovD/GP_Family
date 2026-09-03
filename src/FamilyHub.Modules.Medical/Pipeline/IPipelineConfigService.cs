namespace FamilyHub.Modules.Medical.Pipeline;

/// <summary>Интерфейс над <see cref="PipelineConfigService"/> — та же причина, что у
/// <see cref="IPromptProvider"/>: unit-тесты процессоров подставляют сюда NSubstitute-заглушку
/// вместо поднятия настоящего AppDbContext ради значения, которое в этих тестах всегда "включён"
/// (нет строк PipelineStepConfig).</summary>
public interface IPipelineConfigService
{
    Task<bool> IsEnabledAsync(string pipelineKey, string stepKey, CancellationToken ct = default);

    void Invalidate(string pipelineKey, string stepKey);
}
