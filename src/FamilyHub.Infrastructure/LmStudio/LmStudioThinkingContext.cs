namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Ambient-контекст "какая фоновая задача сейчас вызывает LM Studio" (план "живой поток мыслей") —
/// течёт сам через весь async call-stack (AsyncLocal), поэтому ни один из промежуточных методов
/// между процессором и LmStudioJsonClient (LabSummarizer, OcrNameCorrector, SpecimenResolver,
/// AnalyteSubjectResolver, AnalysisTitleGenerator, PatientReferenceCalculator,
/// LabAnalyteKbSummarizer, MedicationSummarizer — все девять "инструментируемых" мест) не должен
/// знать о job-контексте и передавать его явным параметром сигнатуры. LmStudioJsonClient читает
/// Current, чтобы решить, включать ли поток "мыслей" для очередного вызова (см.
/// SendChatCompletionAsync) — вне этого контекста (HTTP-путь ручного ввода, тесты) Current всегда
/// null, и клиент ведёт себя ровно как раньше (Stream: false, никакой отчётности).
///
/// Два security-гейта (LegitimacyGuardService, AnalytePlausibilityGuardService) вызываются И из
/// фоновых процессоров (внутри уже установленного ambient-контекста), И синхронно из HTTP — этому
/// контексту они не видны иначе, кроме явного suppressThinking: true на самом вызове
/// ExtractJsonAsync (см. class doc там) — ambient-контекст сам по себе не различает "обычный вызов
/// внутри этой задачи" от "гейт внутри этой же задачи".
/// </summary>
public static class LmStudioThinkingContext
{
    private static readonly AsyncLocal<Scope?> Ambient = new();

    public sealed record Scope(LlmJobKind Kind, Guid JobId);

    public static Scope? Current => Ambient.Value;

    /// <summary>Оборачивает RunAsync фонового процессора целиком: `using var _ =
    /// LmStudioThinkingContext.Begin(kind, job.Id);` на весь метод. Восстанавливает предыдущее
    /// значение (сегодня всегда null — вложенных вызовов между этими четырьмя процессорами нет) на
    /// Dispose, а не жёстко сбрасывает в null — безопасно на случай будущей вложенности.</summary>
    public static IDisposable Begin(LlmJobKind kind, Guid jobId)
    {
        var previous = Ambient.Value;
        Ambient.Value = new Scope(kind, jobId);
        return new Restorer(previous);
    }

    private sealed class Restorer(Scope? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
