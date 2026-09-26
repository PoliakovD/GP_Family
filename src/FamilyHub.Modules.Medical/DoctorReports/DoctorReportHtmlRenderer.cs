using System.Globalization;
using System.Net;
using System.Text;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.HealthNotes;

namespace FamilyHub.Modules.Medical.DoctorReports;

/// <summary>
/// ReportModel → самодостаточная HTML-страница для печати (Gotenberg/Chromium → PDF). Без шаблонизатора:
/// строки собираются StringBuilder'ом, весь пользовательский текст проходит HtmlEncode. JS на сайдкаре
/// выключен, поэтому графики — inline-SVG. Шрифты — системные (в образе Gotenberg их набор фиксирован),
/// кириллица есть в Liberation/Noto.
/// </summary>
public static class DoctorReportHtmlRenderer
{
    private const string Disclaimer =
        "Сформировано пациентом в приложении FamilyHub. Не является официальным медицинским документом. " +
        "Резюме анализов составлено автоматически (ИИ) и не заменяет оценку врача.";

    /// <summary>Заголовки включённых блоков в порядке появления в PDF — по ним же публичная страница
    /// показывает «Содержание». Блок с включённой галочкой присутствует всегда (даже без данных: врач
    /// видит, что раздел проверен и пуст).</summary>
    public static IReadOnlyList<string> Sections(ReportModel m)
    {
        var s = new List<string>();
        if (HasComplaints(m)) s.Add("Жалобы и вопросы пациента");
        if (m.Blocks.AiSummaries) s.Add("Резюме анализов (ИИ)");
        if (m.Blocks.Medications) s.Add("Препараты");
        if (m.Blocks.Labs) s.Add("Динамика анализов");
        if (m.Blocks.Measurements) s.Add("Домашние замеры");
        if (m.Blocks.Visits) s.Add("Приёмы врача");
        if (m.Blocks.SymptomsNotes) s.Add("Симптомы и заметки");
        return s;
    }

    private static bool HasComplaints(ReportModel m) =>
        !string.IsNullOrWhiteSpace(m.PatientComment) || m.FlaggedNotes.Count > 0;

    public static string Render(ReportModel m)
    {
        var sb = new StringBuilder(16 * 1024);
        sb.Append("<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>Отчёт для врача</title><style>")
          .Append(Css)
          .Append("</style></head><body>");

        AppendHeader(sb, m);

        var n = 0;
        if (HasComplaints(m)) AppendComplaints(sb, m, ++n);
        if (m.Blocks.AiSummaries) AppendSummaries(sb, m, ++n);
        if (m.Blocks.Medications) AppendMedications(sb, m, ++n);
        if (m.Blocks.Labs) AppendLabs(sb, m, ++n);
        if (m.Blocks.Measurements) AppendMeasurements(sb, m, ++n);
        if (m.Blocks.Visits) AppendVisits(sb, m, ++n);
        if (m.Blocks.SymptomsNotes) AppendSymptomsNotes(sb, m, ++n);

        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>Колонтитул страницы: подпись слева, «стр. X из Y» справа (классы заполняет Chromium).</summary>
    public static string RenderFooter(ReportModel m) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>" +
        "body{margin:0;font-family:'Liberation Sans',Arial,sans-serif;font-size:8px;color:#777}" +
        ".f{display:flex;justify-content:space-between;width:100%;padding:0 0.6in;box-sizing:border-box}" +
        "</style></head><body><div class=\"f\"><span>FamilyHub · отчёт пациента для врача · " +
        E(m.Patient.ShortName) + " · " + Period(m) +
        "</span><span>стр. <span class=\"pageNumber\"></span> из <span class=\"totalPages\"></span></span></div></body></html>";

    // ---- Секции ----

    private static void AppendHeader(StringBuilder sb, ReportModel m)
    {
        var age = Age(m.Patient.BirthDate, m.To);
        sb.Append("<header><div class=\"title\">Отчёт для врача</div><div class=\"sub\">")
          .Append(E(m.Patient.FullName)).Append("</div></header>");

        sb.Append("<table class=\"meta\"><tr><td class=\"k\">Пациент</td><td>").Append(E(m.Patient.FullName)).Append("</td>")
          .Append("<td class=\"k\">Период</td><td>").Append(Period(m)).Append("</td></tr>")
          .Append("<tr><td class=\"k\">Пол, возраст</td><td>")
          .Append(E(string.Join(", ", new[] { m.Patient.Sex, age is null ? null : $"{age} {Years(age.Value)}" }.Where(x => x is not null))))
          .Append(m.Patient.Sex is null && age is null ? "—" : "").Append("</td>")
          .Append("<td class=\"k\">Дата рождения</td><td>")
          .Append(m.Patient.BirthDate is { } b ? Date(b) : "—").Append("</td></tr>")
          .Append("<tr><td class=\"k\">Сформирован</td><td colspan=\"3\">").Append(Date(DateOnly.FromDateTime(m.GeneratedAt))).Append("</td></tr></table>");

        sb.Append("<div class=\"notice\">").Append(E(Disclaimer)).Append("</div>");
    }

    private static void AppendComplaints(StringBuilder sb, ReportModel m, int n)
    {
        Heading(sb, n, "Жалобы и вопросы пациента");
        if (!string.IsNullOrWhiteSpace(m.PatientComment))
            sb.Append("<p class=\"text\">").Append(Paragraphs(m.PatientComment!)).Append("</p>");
        if (m.FlaggedNotes.Count > 0)
        {
            sb.Append("<div class=\"label\">Из дневника пациента:</div><ul>");
            foreach (var note in m.FlaggedNotes)
                sb.Append("<li><span class=\"muted\">").Append(Date(DateOnly.FromDateTime(note.At))).Append("</span> — ")
                  .Append(E(note.Text)).Append("</li>");
            sb.Append("</ul>");
        }
    }

    private static void AppendSummaries(StringBuilder sb, ReportModel m, int n)
    {
        Heading(sb, n, "Резюме анализов (ИИ)");
        if (m.Summaries is not { Count: > 0 })
        {
            Empty(sb, "Готовых резюме анализов за период нет.");
        }
        else
        {
            foreach (var item in m.Summaries)
            {
                sb.Append("<div class=\"card\"><div class=\"card-head\">").Append(Date(item.Date));
                if (!string.IsNullOrWhiteSpace(item.Title)) sb.Append(" · ").Append(E(item.Title!));
                sb.Append("</div>");
                if (!string.IsNullOrWhiteSpace(item.PlainSummary))
                    sb.Append("<p class=\"text\">").Append(Paragraphs(item.PlainSummary!)).Append("</p>");
                if (item.Deviations.Count > 0)
                {
                    sb.Append("<ul>");
                    foreach (var d in item.Deviations)
                        sb.Append("<li><b>").Append(E(d.Name)).Append("</b> — ").Append(E(d.Meaning)).Append("</li>");
                    sb.Append("</ul>");
                }
                sb.Append("</div>");
            }
        }
        if (m.SkippedSummaries > 0)
            Note(sb, $"Ещё {m.SkippedSummaries} {Plural(m.SkippedSummaries, "резюме", "резюме", "резюме")} не включено: показатели изменены, резюме обновляется.");
    }

    private static void AppendMedications(StringBuilder sb, ReportModel m, int n)
    {
        Heading(sb, n, "Препараты");
        if (m.Medications is not { Count: > 0 })
        {
            Empty(sb, "Назначений и приёмов препаратов за период нет.");
            return;
        }

        sb.Append("<table class=\"grid\"><thead><tr><th>Препарат</th><th>Как принимать</th><th>Назначение / приём</th></tr></thead><tbody>");
        foreach (var med in m.Medications)
        {
            var origin = new List<string>();
            if (med.Since is { } since)
                origin.Add(string.IsNullOrWhiteSpace(med.Prescriber)
                    ? $"назначен {Date(since)}"
                    : $"назначил(а) {E(med.Prescriber!)}, {Date(since)}");
            if (med.IntakeCount > 0 && med.LastIntake is { } last)
                origin.Add($"по дневнику: {med.IntakeCount} {Plural(med.IntakeCount, "приём", "приёма", "приёмов")}, последний {Date(DateOnly.FromDateTime(last))}");
            sb.Append("<tr><td><b>").Append(E(med.Name)).Append("</b></td><td>")
              .Append(string.IsNullOrWhiteSpace(med.Dosage) ? "—" : E(med.Dosage!)).Append("</td><td>")
              .Append(origin.Count == 0 ? "—" : string.Join("<br>", origin)).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
    }

    private static void AppendLabs(StringBuilder sb, ReportModel m, int n)
    {
        Heading(sb, n, "Динамика анализов");
        if (m.Labs is not { Rows.Count: > 0 })
        {
            Empty(sb, "Показателей анализов с динамикой или отклонениями за период нет.");
            return;
        }

        var labs = m.Labs;
        sb.Append("<table class=\"grid labs\"><thead><tr><th>Показатель</th>");
        foreach (var d in labs.Dates) sb.Append("<th class=\"num\">").Append(Date(d, withYear: false)).Append("</th>");
        sb.Append("<th>Референс</th></tr></thead><tbody>");
        foreach (var row in labs.Rows)
        {
            sb.Append("<tr><td><b>").Append(E(row.Name)).Append("</b>");
            if (!string.IsNullOrWhiteSpace(row.Unit)) sb.Append("<span class=\"muted\">, ").Append(E(row.Unit!)).Append("</span>");
            sb.Append("</td>");
            foreach (var d in labs.Dates)
            {
                var cell = row.Cells.FirstOrDefault(c => c.Date == d);
                sb.Append("<td class=\"num\">");
                if (cell is null) sb.Append("—");
                else
                {
                    var (cls, arrow) = cell.Flag switch
                    {
                        IndicatorFlag.High => ("hi", " ↑"),
                        IndicatorFlag.Low => ("lo", " ↓"),
                        IndicatorFlag.Critical => ("hi", " ‼"),
                        _ => ("", ""),
                    };
                    sb.Append(cls.Length > 0 ? $"<span class=\"{cls}\">" : "<span>").Append(E(cell.Text)).Append(arrow).Append("</span>");
                }
                sb.Append("</td>");
            }
            sb.Append("<td class=\"muted\">").Append(string.IsNullOrWhiteSpace(row.Reference) ? "—" : E(row.Reference!)).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
        sb.Append("<div class=\"legend\">↑ выше нормы · ↓ ниже нормы · ‼ критично. Показатели с отклонениями — сверху.</div>");
        if (labs.OmittedCount > 0)
            Note(sb, $"Ещё {labs.OmittedCount} {Plural(labs.OmittedCount, "показатель", "показателя", "показателей")} в норме не показаны (единичные измерения).");
    }

    private static void AppendMeasurements(StringBuilder sb, ReportModel m, int n)
    {
        Heading(sb, n, "Домашние замеры");
        var any = false;
        if (m.Metrics is { Count: > 0 })
        {
            any = true;
            sb.Append("<table class=\"grid\"><thead><tr><th>Замер</th><th class=\"num\">Кол-во</th><th class=\"num\">Мин–макс</th><th class=\"num\">Среднее</th><th class=\"num\">Последний</th><th>Динамика</th></tr></thead><tbody>");
            foreach (var t in m.Metrics)
            {
                sb.Append("<tr><td><b>").Append(E(t.Name)).Append("</b><span class=\"muted\">, ").Append(E(t.Unit)).Append("</span></td>")
                  .Append("<td class=\"num\">").Append(t.Count).Append("</td>")
                  .Append("<td class=\"num\">").Append(Range(t)).Append("</td>")
                  .Append("<td class=\"num\">").Append(Pair(t.Avg, t.Avg2)).Append("</td>")
                  .Append("<td class=\"num\">").Append(Pair(t.Last, t.Last2)).Append("<div class=\"muted small\">")
                  .Append(Date(DateOnly.FromDateTime(t.LastAt))).Append("</div></td>")
                  .Append("<td>").Append(Sparkline(t.Series)).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }

        if (m.Wellbeing is { } w)
        {
            any = true;
            sb.Append("<p class=\"text\"><b>Самочувствие:</b> ").Append(w.Count).Append(' ').Append(Plural(w.Count, "запись", "записи", "записей"))
              .Append(", средняя оценка ").Append(Num((decimal)w.Average)).Append(" из 5 (1 — ужасно, 5 — отлично), самая низкая — ")
              .Append(w.Worst).Append(".</p>");
        }
        if (m.Sleep is { } s)
        {
            any = true;
            sb.Append("<p class=\"text\"><b>Сон:</b> ").Append(s.Count).Append(' ').Append(Plural(s.Count, "запись", "записи", "записей"))
              .Append(", в среднем ").Append(Duration(s.AverageMinutes)).Append(", качество ")
              .Append(Num((decimal)s.AverageQuality)).Append(" из 3.</p>");
        }
        if (!any) Empty(sb, "Замеров, самочувствия и сна за период в дневнике нет.");
    }

    private static void AppendVisits(StringBuilder sb, ReportModel m, int n)
    {
        Heading(sb, n, "Приёмы врача");
        if (m.Visits is not { Count: > 0 })
        {
            Empty(sb, "Приёмов врача за период нет.");
            return;
        }

        foreach (var v in m.Visits)
        {
            sb.Append("<div class=\"card\"><div class=\"card-head\">").Append(Date(v.Date));
            if (!string.IsNullOrWhiteSpace(v.Title)) sb.Append(" · ").Append(E(v.Title!));
            if (!string.IsNullOrWhiteSpace(v.Doctor)) sb.Append(" · <span class=\"muted\">").Append(E(v.Doctor!)).Append("</span>");
            sb.Append("</div>");
            LabeledText(sb, "Диагноз", v.Diagnosis);
            LabeledText(sb, "Рекомендации", v.Recommendations);
            LabeledText(sb, "Заметка", v.Description);
            sb.Append("</div>");
        }
    }

    private static void AppendSymptomsNotes(StringBuilder sb, ReportModel m, int n)
    {
        Heading(sb, n, "Симптомы и заметки");
        var any = false;
        if (m.Symptoms is { Count: > 0 })
        {
            any = true;
            sb.Append("<table class=\"grid\"><thead><tr><th>Симптом</th><th class=\"num\">Эпизодов</th><th class=\"num\">Интенсивность (ср. / макс.)</th><th>Где</th><th class=\"num\">Последний</th></tr></thead><tbody>");
            foreach (var s in m.Symptoms)
            {
                var areas = string.Join(", ", s.Areas.Select(a => HealthNoteRules.BodyAreaLabels.GetValueOrDefault(a, a)));
                sb.Append("<tr><td><b>").Append(E(s.Title)).Append("</b></td><td class=\"num\">").Append(s.Episodes)
                  .Append("</td><td class=\"num\">").Append(Num((decimal)s.AverageSeverity)).Append(" / ").Append(s.MaxSeverity).Append(" из 10</td><td>")
                  .Append(areas.Length == 0 ? "—" : E(areas)).Append("</td><td class=\"num\">")
                  .Append(Date(DateOnly.FromDateTime(s.LastAt))).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        if (m.Notes is { Count: > 0 })
        {
            any = true;
            sb.Append("<div class=\"label\">Заметки:</div><ul>");
            foreach (var note in m.Notes)
                sb.Append("<li><span class=\"muted\">").Append(Date(DateOnly.FromDateTime(note.At))).Append("</span> — ")
                  .Append(E(note.Text)).Append("</li>");
            sb.Append("</ul>");
        }
        if (!any) Empty(sb, "Симптомов и заметок за период нет.");
    }

    // ---- Мелочи ----

    private static void Heading(StringBuilder sb, int n, string text) =>
        sb.Append("<h2><span class=\"num-badge\">").Append(n).Append(".</span> ").Append(E(text)).Append("</h2>");

    private static void Empty(StringBuilder sb, string text) => sb.Append("<p class=\"empty\">").Append(E(text)).Append("</p>");

    private static void Note(StringBuilder sb, string text) => sb.Append("<p class=\"legend\">").Append(E(text)).Append("</p>");

    private static void LabeledText(StringBuilder sb, string label, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        sb.Append("<p class=\"text\"><b>").Append(E(label)).Append(":</b> ").Append(Paragraphs(text!)).Append("</p>");
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    /// <summary>Многострочный пользовательский текст: экранируем, переводы строк → &lt;br&gt;.</summary>
    private static string Paragraphs(string s) => E(s.Trim()).Replace("\r\n", "\n").Replace("\n", "<br>");

    private static string Date(DateOnly d, bool withYear = true) =>
        d.ToString(withYear ? "dd.MM.yyyy" : "dd.MM", CultureInfo.InvariantCulture);

    private static string Period(ReportModel m) => $"{Date(m.From)} — {Date(m.To)}";

    private static string Num(decimal v) =>
        Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',');

    private static string Pair(decimal a, decimal? b) => b is null ? Num(a) : $"{Num(a)}/{Num(b.Value)}";

    private static string Range(ReportMetric t) =>
        t.Min2 is { } min2 && t.Max2 is { } max2
            ? $"{Num(t.Min)}–{Num(t.Max)} / {Num(min2)}–{Num(max2)}"
            : $"{Num(t.Min)}–{Num(t.Max)}";

    private static string Duration(int minutes) => minutes >= 60 ? $"{minutes / 60} ч {minutes % 60} мин" : $"{minutes} мин";

    private static int? Age(DateOnly? birth, DateOnly on)
    {
        if (birth is not { } b) return null;
        var age = on.Year - b.Year;
        if (on < b.AddYears(age)) age--;
        return age is < 0 or > 130 ? null : age;
    }

    private static string Years(int n) => Plural(n, "год", "года", "лет");

    private static string Plural(int count, string one, string few, string many)
    {
        var mod100 = count % 100;
        var mod10 = count % 10;
        if (mod100 is >= 11 and <= 14) return many;
        return mod10 == 1 ? one : mod10 is >= 2 and <= 4 ? few : many;
    }

    /// <summary>Мини-график ряда значений: линия + точка последнего значения. Ряд из одной точки — только точка.</summary>
    private static string Sparkline(IReadOnlyList<decimal> values)
    {
        const int w = 96, h = 24, pad = 3;
        if (values.Count == 0) return string.Empty;
        var min = values.Min();
        var max = values.Max();
        var span = max == min ? 1m : max - min;
        double X(int i) => values.Count == 1 ? w / 2.0 : i * (double)(w - 2 * pad) / (values.Count - 1) + pad;
        double Y(decimal v) => h - pad - (double)((v - min) / span) * (h - 2 * pad);

        var points = string.Join(' ', values.Select((v, i) =>
            $"{X(i).ToString("0.#", CultureInfo.InvariantCulture)},{Y(v).ToString("0.#", CultureInfo.InvariantCulture)}"));
        var lastX = X(values.Count - 1).ToString("0.#", CultureInfo.InvariantCulture);
        var lastY = Y(values[^1]).ToString("0.#", CultureInfo.InvariantCulture);
        return $"<svg width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\">" +
               (values.Count > 1 ? $"<polyline points=\"{points}\" fill=\"none\" stroke=\"#0088b0\" stroke-width=\"1.5\"/>" : "") +
               $"<circle cx=\"{lastX}\" cy=\"{lastY}\" r=\"2.2\" fill=\"#0088b0\"/></svg>";
    }

    private const string Css = """
        *{box-sizing:border-box}
        body{margin:0;font-family:'Liberation Serif','Noto Serif',Georgia,'Times New Roman',serif;font-size:10.5pt;line-height:1.45;color:#1c1a19}
        header{border-bottom:2px solid #1c1a19;padding-bottom:6px;margin-bottom:10px}
        .title{font-size:21pt;font-weight:bold;line-height:1.1}
        .sub{font-size:11pt;color:#555}
        table{width:100%;border-collapse:collapse}
        table.meta td{padding:3px 8px 3px 0;vertical-align:top;font-size:10pt}
        table.meta td.k{color:#777;width:104px;white-space:nowrap}
        .notice{margin:10px 0 4px;padding:7px 10px;background:#fdf3e2;border:1px solid #edd3a3;color:#6e4200;font-size:9pt}
        h2{margin:18px 0 6px;font-size:12pt;border-bottom:1px solid #cfcac7;padding-bottom:3px;break-after:avoid}
        .num-badge{color:#0088b0}
        .text{margin:4px 0 8px}
        .label{margin:6px 0 2px;color:#555;font-size:9.5pt}
        ul{margin:2px 0 8px;padding-left:18px}
        li{margin:2px 0}
        .muted{color:#777}.small{font-size:8.5pt}
        .empty{margin:4px 0;color:#777;font-style:italic}
        .legend{margin:4px 0 0;color:#777;font-size:8.5pt}
        .card{border:1px solid #d9d5d2;padding:6px 10px;margin:6px 0;break-inside:avoid}
        .card-head{font-weight:bold;margin-bottom:2px}
        table.grid{margin:4px 0;font-size:9.5pt}
        table.grid th{background:#f1eeec;text-align:left;padding:4px 6px;border:1px solid #d9d5d2;font-size:8.5pt;text-transform:uppercase;letter-spacing:.03em;color:#555}
        table.grid td{padding:4px 6px;border:1px solid #e3dfdc;vertical-align:top}
        table.grid tr{break-inside:avoid}
        .num{text-align:right;white-space:nowrap}
        .hi{color:#a01c1c;font-weight:bold}.lo{color:#0a5c8a;font-weight:bold}
        """;
}
