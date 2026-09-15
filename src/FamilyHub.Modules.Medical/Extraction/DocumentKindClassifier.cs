using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Prompts;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Итог классификации — <see cref="IsTransientFailure"/> зеркалит SpecimenResolver/
/// LegitimacyGuardService: сбой ТЕХНИЧЕСКИЙ (LM Studio недоступен), не смысловой (модель не
/// уверена) — вызывающая сторона (LmStudioMedicalDocumentExtractor) обязана пробросить его как
/// исключение, а не молча остаться на провизорном виде, иначе Hangfire не повторит задачу для
/// временно недоступного сервера.</summary>
public record DocumentKindClassification(MedicalRecordKind? Kind, string? Reason, bool IsTransientFailure = false);

/// <summary>
/// Определение вида документа (анализ / посещение врача) для батч-загрузки — единственный
/// потребитель: пользователь при батче не выбирает вид, MedicalRecord создаётся с провизорным
/// Kind (по вкладке, откуда загружали) и KindIsAutoDetected=true, а фактический вид определяет
/// эта модель по содержимому файла, один LLM-вызов на документ, тем же приёмом, что
/// SpecimenResolver/AnalyteSubjectResolver (отдельный узкий проход, не побочное поле промпта
/// структурирования — совмещение задач мешало бы обеим). Вызывается ДО выбора системного промпта
/// analysis.extract/visit.extract, поэтому дальше по конвейеру ничего не переигрывается.
/// </summary>
public class DocumentKindClassifier(
    ILmStudioJsonClient client, IPromptProvider promptProvider, ILogger<DocumentKindClassifier> logger)
{
    /// <summary>Шапка документа почти всегда достаточна — вид определяется формой бланка
    /// (таблица показателей vs текст заключения), не его концом.</summary>
    private const int HeaderChars = 2000;

    private const string SystemPrompt = """
        Ты — классификатор типа медицинского документа. На входе — текст (может быть частью
        документа) или фото. Определи, это БЛАНК ЛАБОРАТОРНОГО АНАЛИЗА (таблица показателей со
        значениями/единицами измерения/референсами — общий анализ крови, биохимия, ПЦР, УЗИ с
        числовыми параметрами и т.п.) или ЗАКЛЮЧЕНИЕ/ВЫПИСКА ВРАЧА (текст приёма — диагноз, анамнез,
        рекомендации, назначения, без таблицы показателей с числовыми значениями). Верни ТОЛЬКО
        валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа:
        {"kind": "analysis" или "visit", "confidence": 0.0, "reason": "короткое обоснование по-русски"}

        Правила:
        - "kind": "analysis" — документ ПРЕИМУЩЕСТВЕННО таблица показателей (даже если в шапке
          есть направившим врач и краткий комментарий) — таблица со значениями решает.
        - "kind": "visit" — документ ПРЕИМУЩЕСТВЕННО текст: диагноз, жалобы, осмотр, рекомендации,
          назначенные препараты — без таблицы числовых показателей с референсами.
        - "confidence" — число от 0 до 1, твоя уверенность именно в выборе между этими двумя видами.
        - Если документ смешанный или неопределимый — выбери вид, который преобладает по объёму, и
          понизь confidence соответственно.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    public async Task<DocumentKindClassification> ClassifyAsync(DocumentContent content, CancellationToken ct = default)
    {
        var prompt = await promptProvider.GetAsync("document.kind-classify", SystemPrompt, ct);

        LmStudioJsonResult result;
        if (content.Kind == DocumentSourceKind.Text && !string.IsNullOrEmpty(content.Text))
        {
            var header = content.Text.Length > HeaderChars ? content.Text[..HeaderChars] : content.Text;
            result = await client.ExtractJsonAsync(prompt, header, ct);
        }
        else if (content.Kind == DocumentSourceKind.Image && content.Images.Count > 0)
        {
            var first = content.Images[0];
            result = await client.ExtractJsonAsync(
                prompt, "Определи вид этого медицинского документа.", [(first.Bytes, first.ContentType)], ct);
        }
        else
        {
            return new DocumentKindClassification(null, "Документ не содержит ни текста, ни изображений.");
        }

        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Классификация вида документа недоступна: {Error}", result.Error);
            return new DocumentKindClassification(null, result.Error, result.IsTransient);
        }

        var kindText = ReadString(result.Payload, "kind");
        var kind = kindText?.Trim().ToLowerInvariant() switch
        {
            "analysis" => (MedicalRecordKind?)MedicalRecordKind.Analysis,
            "visit" => MedicalRecordKind.DoctorVisit,
            _ => null,
        };
        return new DocumentKindClassification(kind, ReadString(result.Payload, "reason"));
    }
}
