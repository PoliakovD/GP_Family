using System.Text;
using FamilyHub.Domain.ValueObjects;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Страховка от главной путаницы бланка (план "качество ИИ-распознавания анализов", Этап 2): на
/// типичном бланке лаборатории — как раз то ФИО, что подставляет модель в поле "doctor" — на
/// самом деле напечатано рядом с меткой "Пациент"/"Ф.И.О."/"ФИО пациента", а направившего врача
/// на бланке часто попросту нет (или указана только должность заведующего лабораторией, тоже не
/// лечащий врач). Промпт analysis.extract/visit.extract просит модель не путать их, но
/// полагаться ТОЛЬКО на промпт для маленькой модели недостаточно — этот код детерминированно
/// СРАВНИВАЕТ извлечённое "doctor" с реальным ФИО пациента ЗАПИСИ (PatientIdentityResolver) и,
/// при совпадении, велит вызывающему коду (MedicalDocumentExtractionProcessor) отбросить значение
/// вместо того, чтобы молча записать пациента в поле врача.
///
/// Сравнение — по подстроке нормализованных токенов, не по точному равенству: врач на бланке
/// может быть напечатан в любом из трёх стилей ФИО (см. PersonName.Format — полностью, с
/// сокращённым отчеством, одними инициалами) и может идти с хвостом вроде специальности
/// ("Иванов И.И., терапевт") — нормализованная форма пациента при этом обязана целиком
/// содержаться в нормализованной строке "doctor" (не наоборот и не как набор токенов без учёта
/// повторов — иначе "Иванов И.И." и "Иванов А.И." при схлопывании инициалов в множество стали бы
/// неотличимы, см. историю правок).
/// </summary>
public static class PatientDoctorNameGuard
{
    public static bool IsPatientName(
        string? extractedDoctor, string? patientFirstName, string? patientLastName, string? patientMiddleName)
    {
        if (string.IsNullOrWhiteSpace(extractedDoctor)) return false;
        if (string.IsNullOrWhiteSpace(patientLastName) && string.IsNullOrWhiteSpace(patientFirstName)) return false;

        var normalizedDoctor = Normalize(extractedDoctor);
        if (normalizedDoctor.Length == 0) return false;

        ReadOnlySpan<PersonNameStyle> styles = [PersonNameStyle.Full, PersonNameStyle.ShortPatronymic, PersonNameStyle.Initials];
        foreach (var style in styles)
        {
            var candidate = Normalize(PersonName.Format(patientLastName, patientFirstName, patientMiddleName, style));
            if (candidate.Length > 0 && normalizedDoctor.Contains(candidate, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Нижний регистр, ё→е (тот же приём, что LabAnalyteNormalizer), любой символ, не
    /// буква/цифра, — пробелом, схлопнутые пробелы по краям обрезаны: "Иванов И.И.", "ИВАНОВ И. И.",
    /// "иванов, и.и." нормализуются в одну и ту же строку "иванов и и".</summary>
    private static string Normalize(string value)
    {
        var lower = value.ToLowerInvariant().Replace('ё', 'е');
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
