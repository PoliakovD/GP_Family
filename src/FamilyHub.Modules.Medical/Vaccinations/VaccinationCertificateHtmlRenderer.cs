using System.Net;
using System.Text;
using FamilyHub.Domain.Vaccinations;

namespace FamilyHub.Modules.Medical.Vaccinations;

/// <summary>PersonScheduleDto → самодостаточная HTML-страница для печати (Gotenberg/Chromium → PDF) —
/// тот же приём, что DoctorReportHtmlRenderer: без шаблонизатора, весь текст через HtmlEncode.</summary>
public static class VaccinationCertificateHtmlRenderer
{
    public static string Render(PersonScheduleDto schedule)
    {
        var sb = new StringBuilder(8 * 1024);
        sb.Append("<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>Сертификат прививок</title><style>")
          .Append(Css).Append("</style></head><body>");

        sb.Append("<header><div class=\"title\">Сертификат прививок</div><div class=\"sub\">")
          .Append(E(schedule.Subject.Name)).Append("</div></header>");
        sb.Append("<div class=\"notice\">Сформировано пациентом в приложении FamilyHub на основе внесённых им данных. " +
                   "Не является официальным медицинским документом.</div>");

        var rows = schedule.ByAge.Concat(schedule.Custom)
            .Where(i => i.Status is VaccinationStatus.Done or VaccinationStatus.HadDisease)
            .OrderBy(i => i.Date ?? DateOnly.MinValue)
            .ToList();

        if (rows.Count == 0)
        {
            sb.Append("<p class=\"empty\">Отметок о сделанных прививках пока нет.</p>");
        }
        else
        {
            sb.Append("<table class=\"grid\"><thead><tr><th>Прививка</th><th>Доза</th><th>Дата</th></tr></thead><tbody>");
            foreach (var item in rows)
            {
                sb.Append("<tr><td><b>").Append(E(item.SeriesName)).Append("</b></td><td>").Append(E(item.Label)).Append("</td><td>")
                  .Append(item.Date is { } d
                      ? d.ToString("dd.MM.yyyy")
                      : item.Status == VaccinationStatus.HadDisease ? "перенесённая болезнь" : "—")
                  .Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    private const string Css = """
        *{box-sizing:border-box}
        body{margin:0;font-family:'Liberation Serif','Noto Serif',Georgia,'Times New Roman',serif;font-size:10.5pt;line-height:1.45;color:#1c1a19}
        header{border-bottom:2px solid #1c1a19;padding-bottom:6px;margin-bottom:10px}
        .title{font-size:21pt;font-weight:bold;line-height:1.1}
        .sub{font-size:11pt;color:#555}
        .notice{margin:10px 0;padding:7px 10px;background:#fdf3e2;border:1px solid #edd3a3;color:#6e4200;font-size:9pt}
        table{width:100%;border-collapse:collapse;font-size:9.5pt}
        th{background:#f1eeec;text-align:left;padding:4px 6px;border:1px solid #d9d5d2;font-size:8.5pt;text-transform:uppercase;letter-spacing:.03em;color:#555}
        td{padding:4px 6px;border:1px solid #e3dfdc;vertical-align:top}
        .empty{color:#777;font-style:italic}
        """;
}
