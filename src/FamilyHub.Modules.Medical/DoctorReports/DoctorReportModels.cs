using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Extraction;

namespace FamilyHub.Modules.Medical.DoctorReports;

/// <summary>Какие блоки вошли в отчёт (галочки при создании).</summary>
public record ReportBlocks(
    bool Labs, bool AiSummaries, bool Medications, bool Visits, bool Measurements, bool SymptomsNotes)
{
    public bool Any => Labs || AiSummaries || Medications || Visits || Measurements || SymptomsNotes;
}

/// <summary>Сколько данных попадёт в отчёт за период — счётчик под выбором периода в форме.
/// FlaggedNotes — заметки дневника с пометкой «в вопросы к врачу»: они попадут в блок жалоб.</summary>
public record ReportCounts(int Analyses, int Visits, int DiaryEntries, int FlaggedNotes);

/// <summary>Пациент на момент формирования. Sex — «м»/«ж»/null.</summary>
public record ReportPatient(string FullName, string ShortName, string? Sex, DateOnly? BirthDate);

public record ReportLabCell(DateOnly Date, string Text, IndicatorFlag Flag);

public record ReportLabRow(string Name, string? Unit, string? Reference, IReadOnlyList<ReportLabCell> Cells, bool HasDeviation);

public record ReportLabTable(IReadOnlyList<DateOnly> Dates, IReadOnlyList<ReportLabRow> Rows, int OmittedCount);

public record ReportSummaryItem(DateOnly Date, string? Title, string? PlainSummary, IReadOnlyList<LabSummaryDeviation> Deviations);

public record ReportVisit(DateOnly Date, string? Doctor, string? Title, string? Description, string? Diagnosis, string? Recommendations);

/// <summary>Препарат: из назначений визитов (Since/Prescriber) и/или из приёмов дневника (IntakeCount/LastIntake).</summary>
public record ReportMedication(
    string Name, string? Dosage, DateOnly? Since, string? Prescriber, int IntakeCount, DateTime? LastIntake);

public record ReportMetric(
    string Code, string Name, string Unit, int Count,
    decimal Min, decimal Max, decimal Avg, decimal Last, DateTime LastAt,
    decimal? Min2, decimal? Max2, decimal? Avg2, decimal? Last2,
    IReadOnlyList<decimal> Series);

public record ReportWellbeing(int Count, double Average, int Worst);

public record ReportSleep(int Count, int AverageMinutes, double AverageQuality);

public record ReportSymptom(string Title, int Episodes, double AverageSeverity, int MaxSeverity, DateTime LastAt, IReadOnlyList<string> Areas);

public record ReportNote(DateTime At, string Text);

/// <summary>Всё, что попадает в PDF. Необязательные блоки null, если выключены галочкой; пустые
/// (включён, но данных нет) — пустые списки, и рендер решает, показывать ли заголовок.</summary>
public record ReportModel(
    ReportPatient Patient,
    DateOnly From,
    DateOnly To,
    DateTime GeneratedAt,
    ReportBlocks Blocks,
    string? PatientComment,
    IReadOnlyList<ReportNote> FlaggedNotes,
    ReportLabTable? Labs,
    IReadOnlyList<ReportSummaryItem>? Summaries,
    int SkippedSummaries,
    IReadOnlyList<ReportMedication>? Medications,
    IReadOnlyList<ReportVisit>? Visits,
    IReadOnlyList<ReportMetric>? Metrics,
    ReportWellbeing? Wellbeing,
    ReportSleep? Sleep,
    IReadOnlyList<ReportSymptom>? Symptoms,
    IReadOnlyList<ReportNote>? Notes)
{
    /// <summary>Есть ли в отчёте хоть что-то, кроме шапки — пустой PDF пользователю не нужен.</summary>
    public bool HasContent =>
        !string.IsNullOrWhiteSpace(PatientComment)
        || FlaggedNotes.Count > 0
        || Labs is { Rows.Count: > 0 }
        || Summaries is { Count: > 0 }
        || Medications is { Count: > 0 }
        || Visits is { Count: > 0 }
        || Metrics is { Count: > 0 }
        || Wellbeing is not null
        || Sleep is not null
        || Symptoms is { Count: > 0 }
        || Notes is { Count: > 0 };
}
