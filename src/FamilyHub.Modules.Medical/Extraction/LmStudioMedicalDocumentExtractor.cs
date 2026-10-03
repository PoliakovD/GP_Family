using System.Text;
using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Prompts;
using FamilyHub.Modules.Medical.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Реализация конвейера извлечения через локальный LM Studio (ветка medicalrecords, задачи
/// 5.2/5.3). Диспетчеризация формата — <see cref="IDocumentTextExtractor"/> (Infrastructure):
/// текстовый путь дёшев и точен, vision-OCR — только для фото и PDF-сканов (см. план). Длинный
/// документ режется на куски (текст) или обрабатывается постранично (картинки) — один вызов
/// модели на кусок/страницу, результаты сливаются с дедупом.
///
/// Антигаллюцинационный гейт для анализов (прецедент — MedicationSummarizer): на текстовом пути
/// имя каждого извлечённого показателя обязано нормализованно встречаться в исходном тексте
/// куска — показатель, которого модель "не увидела" в тексте, а придумала, отбрасывается. На
/// vision-пути такой проверки нет по построению (исходный текст недоступен) — точность там ниже
/// принципиально, поэтому текстовый путь предпочтителен всегда, когда доступен.
///
/// Первый шаг конвейера (PipelineCatalog.LegitimacyCheckStep, см. LegitimacyGuardService) —
/// документ ЦЕЛИКОМ (текст или изображения) проверяется на попытку prompt injection ДО того, как
/// хоть один его фрагмент дойдёт до analysis.extract/visit.extract: содержимое документа
/// полностью контролируется тем, кто его загрузил.
/// </summary>
public class LmStudioMedicalDocumentExtractor(
    IDocumentTextExtractor documentTextExtractor,
    ILmStudioJsonClient lmStudioClient,
    SpecimenResolver specimenResolver,
    AnalyteSubjectResolver subjectResolver,
    AnalysisTitleGenerator titleGenerator,
    ILegitimacyGuardService legitimacyGuard,
    DocumentKindClassifier kindClassifier,
    IPromptProvider promptProvider,
    IPipelineConfigService pipelineConfig,
    IOptions<ExtractionOptions> options,
    ILogger<LmStudioMedicalDocumentExtractor> logger) : IMedicalDocumentExtractor
{
    private const int ChunkOverlapChars = 200;

    /// <summary>Сколько строк-кандидатов таблицы (LabTableRowDetector) идёт в одном вызове модели
    /// (см. ExtractIndicatorsByRowsAsync) — небольшой батч, а не вся таблица разом: чем длиннее
    /// список, тем выше у маленькой модели шанс потерять/перепутать строку где-то в середине.</summary>
    private const int RowBatchSize = 8;

    /// <summary>Сколько раз повторить строки, пропущенные или не прошедшие сверку на предыдущем
    /// проходе, — уже по одной строке за вызов (узкая задача, выше шанс успеха), не тем же
    /// батчем. 1 — один дополнительный проход поверх основного (общий бюджет вызовов на документ
    /// остаётся ограниченным).</summary>
    private const int MaxRowRetryPasses = 1;

    /// <summary>Потолок длины имени показателя (см. ParseIndicators) — отсекает случаи, когда
    /// модель вернула предложение/пояснение вместо названия, не полные (но настоящие) составные
    /// названия лабораторных тестов реальных бланков.</summary>
    private const int MaxIndicatorNameLength = 160;

    /// <summary>Промпт текстового пути (см. BuildRowBatchUserText/ExtractIndicatorsByRowsAsync) —
    /// вход не сырой текст куска бланка, а пронумерованные строки-кандидаты, которые уже нашёл
    /// детерминированный LabTableRowDetector (план "качество ИИ-распознавания анализов", Этап 1):
    /// раньше маленькая модель получала целый чанк текста и сама решала, что в нём вообще есть
    /// показатель, а что нет — на реальном бланке (13 показателей) это давало 3, потому что часть
    /// строк модель просто не замечала в потоке текста. Теперь ей заранее сказано, СКОЛЬКО строк
    /// и какие именно нужно обработать (задача "заполни бланк", а не "найди, что заполнять") — а
    /// код после ответа сверяет, что каждая заявленная строка получила результат, и повторяет
    /// пропущенные (см. ExtractIndicatorsByRowsAsync). У vision-пути (фото/скан без текстового
    /// слоя) строк-кандидатов нет — там этот промпт используется в старом режиме "вот текст/фото,
    /// найди все показатели сам" (см. ExtractAnalysisAsync), поэтому раздел "Формат строки" ниже
    /// применим только когда во входе реально есть "[R..] ...".</summary>
    private const string AnalysisSystemPrompt = """
        Ты — оцифровщик бланков лабораторных анализов. На входе — либо пронумерованные строки
        таблицы результатов (каждая вида "[ID] Название | Значение | Единица измерения |
        Референсные значения" — единица измерения и референс могут отсутствовать в конкретной
        строке), либо (если строк с "[ID]" нет) обычный текст или фото бланка целиком — тогда
        сам найди в нём показатели. Верни ТОЛЬКО валидный JSON, без пояснений, без markdown, без
        блока <think>.

        Формат ответа:
        {
          "indicators": [
            {
              "rowId": "ID строки как во входных данных (например, \"R5\") — заполняй ТОЛЬКО когда вход был пронумерованными строками, иначе null",
              "name": "название показателя БЕЗ порядкового номера пункта бланка и БЕЗ единицы измерения (например, \"Гемоглобин\", не \"1. Гемоглобин, г/л\")",
              "value": "значение как напечатано, БЕЗ стрелки тренда ↑/↓ перед ним (например, \"118\" или \"отрицательно\", не \"↓ 1.2\" — стрелка не часть значения)",
              "unit": "единица измерения или null (например, \"г/л\")",
              "refLow": 130,
              "refHigh": 160,
              "refText": "референсный диапазон текстом или null — заполняй ТОЛЬКО если референс НЕ раскладывается на refLow/refHigh (например, \"отрицательно\", \"1-3 в п/зр\")",
              "refExpected": "ожидаемый НОРМАЛЬНЫЙ результат этого показателя по общемедицинским знаниям — заполняй ТОЛЬКО если в бланке референса нет вовсе (ни числом, ни текстом); если референс в бланке есть — всегда null"
            }
          ],
          "documentDate": "дата анализа/забора материала, как указана в бланке, в формате YYYY-MM-DD, или null",
          "doctor": "ФИО и/или специальность врача, назначившего анализ, если указаны в бланке — иначе null, не придумывай"
        }

        Правила:
        - Если вход — пронумерованные строки: ОБЯЗАТЕЛЬНО верни один объект на КАЖДУЮ переданную
          строку, у которой есть осмысленное значение, — не пропускай строки, даже если их много.
          "rowId" каждого объекта обязан быть ID одной из переданных строк — не добавляй
          показатели, которых не было в списке, и не меняй значение на другое, чем напечатано в
          этой конкретной строке.
        - Извлекай ТОЛЬКО то, что реально написано во входных данных — ничего не добавляй от себя
          и не переноси показатели из общих знаний о медицине.
        - "name" — название показателя БЕЗ порядкового номера строки/пункта бланка ("1.", "12)" и
          т.п. в начале — это нумерация бланка, не часть названия) и без единицы измерения (она
          отдельным полем "unit"). Регистр — как обычно пишут в литературном тексте (с заглавной
          буквы), даже если в бланке весь текст напечатан КАПСОМ. Исключение — АББРЕВИАТУРЫ
          (СРБ, АЧТВ, МНО, ТТГ, HbA1c, IgG): оставляй ровно как в бланке, не переводи в "Срб"/"Ачтв".
        - Если у показателя нет значения (пустая ячейка, только название без цифры или текста
          напротив, ЛИБО там стоит только прочерк "-"/"—") — НЕ включай его в ответ вообще,
          пропусти: прочерк ничего не говорит о результате анализа, хранить его бессмысленно. Если
          же явно написано СЛОВОМ "отсутствуют", "не обнаружено" или "отрицательно" — это
          осмысленный качественный РЕЗУЛЬТАТ анализа, а не пустая ячейка: включай показатель с ним
          как есть — для него тоже можно заполнить "refExpected" (см. ниже), если референса нет.
        - "refLow"/"refHigh" — числа, только если референс — диапазон (например, "130-160"), В ТОМ
          ЧИСЛЕ односторонний: "<47"/"до 47"/"менее 47"/"не более 47" → refLow=0, refHigh=47;
          ">47"/"от 47"/"более 47"/"не менее 47" → refLow=47, refHigh оставь null (верхней границы
          нет). "refText" при этом заполни ТОЖЕ — референс как напечатан буквально (чтобы
          пользователь видел исходную формулировку, не "0-47"). Если референс не раскладывается ни
          на диапазон, ни на такую одностороннюю форму (например, "отрицательно", "1-3 в п/зр") —
          заполни только "refText", "refLow"/"refHigh" оставь null.
        - "refExpected" — заполняй ТОЛЬКО когда у показателя референса нет ВООБЩЕ (ни
          refLow/refHigh, ни refText) — тогда, если ты уверен, укажи ожидаемый нормальный результат
          по общемедицинским знаниям (например, для теста на инфекцию — "не обнаружено", для
          показателя с типичной нормой — сам диапазон текстом, "3,5-5,0"). Если референс есть в
          любом виде — "refExpected" всегда null. Не придумывай норму для показателя, в котором сам
          не уверен — лучше null, чем ошибочная подсказка.
        - "documentDate"/"doctor" — заполняй, только если это ДЕЙСТВИТЕЛЬНО есть во входных данных
          (обычно в шапке документа); если во входе только строки таблицы без шапки, оставь оба
          null.
        - Если во входе нет ни одного показателя анализа — indicators пустой массив, но
          documentDate/doctor всё равно заполни, если они есть.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    private const string VisitSystemPrompt = """
        Ты — оцифровщик заключений и выписок врача. На входе — текст или фото документа (может
        быть только часть документа, если он большой). Извлеки структурированное содержимое приёма
        и верни ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа:
        {
          "diagnosis": "диагноз как указан в документе или null",
          "anamnesis": "анамнез — жалобы, история заболевания со слов пациента, если записаны врачом, или null",
          "proceduresPerformed": "манипуляции/анализы, выполненные ПРЯМО НА ЭТОМ приёме (осмотр, измерения, взятые пробы и т.п.) или null",
          "recommendations": "рекомендации врача (немедикаментозные — режим, диета, повторный визит и т.п.) или null",
          "prescriptions": [
            {
              "name": "название назначенного препарата как написано в документе",
              "dosageInstructions": "как принимать — доза, кратность, длительность, как написано в документе, или null, если не указано"
            }
          ],
          "documentDate": "дата приёма/выписки, как указана в документе, в формате YYYY-MM-DD, или null",
          "suggestedTitle": "короткое название документа, если оно прямо напечатано (например, \"Выписка невролога\") — иначе null, не придумывай",
          "doctor": "ФИО и/или специальность принимавшего врача, если указаны в документе — иначе null, не придумывай"
        }

        Правила:
        - Заполняй поле только если соответствующая информация реально есть в этом фрагменте —
          иначе null (для "prescriptions" — пустой массив). Не додумывай.
        - "prescriptions" — только препараты, реально НАЗНАЧЕННЫЕ в этом документе, не путай с
          "anamnesis" (что пациент уже принимал раньше) или "proceduresPerformed".
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    public async Task<ExtractionResult> ExtractAsync(DocumentSource source, MedicalRecordKind? kind, CancellationToken ct = default)
    {
        var content = await documentTextExtractor.ExtractAsync(source.Content, source.ContentType, ct);
        if (content.Kind == DocumentSourceKind.Unsupported)
        {
            logger.LogInformation(
                "Распознавание «{FileName}» невозможно: {Reason}", source.FileName, content.UnsupportedReason);
            return new ExtractionResult(false, null, null, content.UnsupportedReason);
        }

        // Первый обязательный шаг конвейера (PipelineCatalog.LegitimacyCheckStep) — документ
        // ЦЕЛИКОМ, до того как хоть один его фрагмент попадёт в системный промпт
        // analysis.extract/visit.extract: содержимое документа полностью контролируется тем, кто
        // его загрузил (это "бланк", напечатанный кем угодно), включая попытку внедрить в него
        // инструкцию для модели, которая будет его читать.
        var guardResult = content.Kind == DocumentSourceKind.Text
            ? await legitimacyGuard.CheckAsync(content.Text!, ct)
            : await legitimacyGuard.CheckAsync(
                string.Empty, content.Images.Select(i => (i.Bytes, i.ContentType)).ToList(), ct);
        if (!guardResult.IsLegitimate)
        {
            logger.LogWarning(
                "Распознавание «{FileName}» остановлено проверкой легитимности: {Reason}", source.FileName, guardResult.Reason);
            return new ExtractionResult(false, null, null, guardResult.Reason, IsTransientFailure: guardResult.IsTransientFailure);
        }

        // kind=null — батч-загрузка (MedicalRecord.KindIsAutoDetected): вид ещё не выбран
        // пользователем, определяем его отдельным узким проходом ДО выбора системного промпта
        // ниже (см. DocumentKindClassifier). Технический сбой пробрасывается как исключение —
        // тем же приёмом, что и LmStudioUnavailableException в MedicalDocumentExtractionProcessor,
        // чтобы Hangfire реально повторил задачу, а не молча "угадал" вид для временно
        // недоступного сервера. Модель не уверена/ответ мусорный — остаёмся на Analysis (дефолт
        // MedicalRecordKind, см. class doc енама) — редкий случай, поправимый пользователем вручную.
        var resolvedKind = kind;
        if (resolvedKind is null && await pipelineConfig.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "kind-classify", ct))
        {
            var classification = await kindClassifier.ClassifyAsync(content, ct);
            if (classification.IsTransientFailure)
                throw new LmStudioUnavailableException(classification.Reason ?? "Локальный сервер распознавания недоступен.");
            resolvedKind = classification.Kind;
        }
        resolvedKind ??= MedicalRecordKind.Analysis;

        var result = resolvedKind == MedicalRecordKind.Analysis
            ? await ExtractAnalysisAsync(content, ct)
            : await ExtractVisitAsync(content, ct);
        return result with { Kind = resolvedKind.Value };
    }

    private async Task<ExtractionResult> ExtractAnalysisAsync(DocumentContent content, CancellationToken ct)
    {
        var indicators = new List<ExtractedLabIndicator>();
        // Транзиентным весь документ считаем, только если НИ ОДИН структурирующий вызов не смог
        // ответить (сервер лёг между гейтом легитимности и структурированием) И хотя бы один упал
        // именно технически — частичный успех (часть кусков дала показатели, часть упала
        // технически) не повод проваливать весь файл, отсутствующая часть просто не попадёт в
        // результат этого прогона.
        var hadAnySuccessfulCall = false;
        var hadAnyTransientFailure = false;

        // Поля уровня документа (documentDate/doctor) обычно есть только в ШАПКЕ бланка — первый
        // чанк/страница, где модель их реально нашла, побеждает; остальные куски (таблица
        // показателей без шапки) просто не заполняют эти поля повторно. Источник показателя
        // (биоматериал/исследование) и короткое название анализа сюда больше не входят —
        // резолвятся отдельными проходами (см. SpecimenResolver/AnalysisTitleGenerator), не как
        // побочные поля промпта структурирования (заметки 1/4 — совмещение задач мешало всем).
        DateOnly? documentDate = null;
        string? doctor = null;

        void CaptureDocumentFields(Dictionary<string, JsonElement> payload)
        {
            documentDate ??= ParseDate(ReadString(payload, "documentDate"));
            doctor ??= ReadString(payload, "doctor");
        }

        var analysisPrompt = await promptProvider.GetAsync("analysis.extract", AnalysisSystemPrompt, ct);
        ExtractionRowCoverage? rowCoverage = null;

        if (content.Kind == DocumentSourceKind.Text)
        {
            var tableRows = LabTableRowDetector.Detect(content.Text!).Rows;
            if (tableRows.Count > 0)
            {
                // Основной путь (план "качество ИИ-распознавания анализов", Этап 1): строки-кандидаты
                // уже нашёл детерминированный LabTableRowDetector — модель получает их пронумерованными
                // батчами и обязана дать результат на каждую (см. ExtractIndicatorsByRowsAsync), вместо
                // того чтобы самой решать, есть ли вообще в куске текста что распознавать.
                var outcome = await ExtractIndicatorsByRowsAsync(tableRows, analysisPrompt, CaptureDocumentFields, ct);
                indicators.AddRange(outcome.Indicators);
                hadAnySuccessfulCall |= outcome.HadAnySuccessfulCall;
                hadAnyTransientFailure |= outcome.HadAnyTransientFailure;
                rowCoverage = new ExtractionRowCoverage(
                    tableRows.Count, tableRows.Count - outcome.UnmatchedRows.Count,
                    outcome.UnmatchedRows.Select(r => r.RawLine).ToList());
            }
            else
            {
                // Fallback (план, Этап 1, пункт 5, упрощённо): детектор не нашёл ни одной строки —
                // бланк нестандартный (не похож на таблицу с узнаваемой шапкой колонок). Старый режим
                // "весь чанк текста, найди показатели сам" — тот же промпт понимает и голый текст на
                // входе (см. его докстринг) — чтобы такой документ не остался вовсе без распознавания,
                // пока эвристика детектора не научится его понимать (донастройка — через eval-стенд,
                // Этап 0 плана, а не вслепую).
                foreach (var chunk in SplitIntoChunks(content.Text!, options.Value.MaxCharsPerChunk, ChunkOverlapChars))
                {
                    var result = await lmStudioClient.ExtractJsonAsync(analysisPrompt, chunk, ct);
                    if (!result.Success || result.Payload is null)
                    {
                        if (result.IsTransient) hadAnyTransientFailure = true;
                        continue;
                    }
                    hadAnySuccessfulCall = true;

                    CaptureDocumentFields(result.Payload);
                    foreach (var (_, indicator) in ParseIndicators(result.Payload))
                    {
                        // Антигаллюцинационный гейт (fallback без строк-кандидатов): имя показателя
                        // обязано встречаться в исходном тексте — иначе модель его придумала.
                        if (!chunk.Contains(indicator.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        indicators.Add(indicator);
                    }
                }
            }
        }
        else
        {
            foreach (var image in content.Images)
            {
                var result = await lmStudioClient.ExtractJsonAsync(
                    analysisPrompt, "Распознай показатели анализа на этом изображении.", [(image.Bytes, image.ContentType)], ct);
                if (!result.Success || result.Payload is null)
                {
                    if (result.IsTransient) hadAnyTransientFailure = true;
                    continue;
                }
                hadAnySuccessfulCall = true;

                CaptureDocumentFields(result.Payload);
                indicators.AddRange(ParseIndicators(result.Payload).Select(p => p.Indicator));
            }
        }

        // Резолвинг источника — отдельный LLM-вызов на весь этот файл (не на чанк/страницу, не
        // побочное поле промпта структурирования выше, см. SpecimenResolver). Ещё не сведён к
        // ссылке на справочник — это делает MedicalDocumentExtractionProcessor, у которого есть
        // доступ к БД (этот класс — чистый LLM-клиент, без Infrastructure.Persistence).
        // Необязательный шаг (§2 плана) — выключен из админки означает, что показатели этого
        // документа остаются с нерезолвленным источником (SpecimenContextIds.Unresolved,
        // проставляется дальше по конвейеру), а не что модель спрашивается впустую.
        var specimenResolution = await pipelineConfig.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "specimen-resolve", ct)
            ? await specimenResolver.ResolveAsync(content, ct)
            : SpecimenDocumentResolution.Empty;

        var deduped = DeduplicateByName(indicators);
        if (deduped.Count == 0)
        {
            var isTransientFailure = hadAnyTransientFailure && !hadAnySuccessfulCall;
            return new ExtractionResult(
                true, [], null, "Не удалось распознать ни одного показателя.", documentDate, null, doctor,
                specimenResolution, isTransientFailure, RowCoverage: rowCoverage);
        }

        // Короткое название — отдельный проход по шапке + реальному составу показателей (заметка 4),
        // не побочное поле промпта структурирования выше. Необязательный шаг (§2 плана) — выключен
        // из админки означает, что название остаётся null (правится вручную через "Редактировать",
        // не восполняется откуда-то ещё).
        var suggestedTitle = await pipelineConfig.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "title", ct)
            ? await titleGenerator.GenerateAsync(content, deduped.Select(d => d.Name).ToList(), ct)
            : null;

        // Уточнение родового названия по разделу "Оказанные услуги" (см. AnalyteSubjectResolver) —
        // отдельный проход по реальному составу показателей ЭТОГО файла, тем же приёмом, что и
        // title/specimen-resolve выше: совмещение задач в одном вызове мешало бы всем. Необязательный
        // шаг (§2 плана) — выключен из админки означает, что показатели остаются с родовым именем
        // с бланка (MedicalDocumentExtractionProcessor разводит коллизии между файлами сам, см.
        // AnalyteKeyDisambiguator).
        var subjectResolution = await pipelineConfig.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "subject-resolve", ct)
            ? await subjectResolver.ResolveAsync(content, deduped.Select(d => d.Name).ToList(), ct)
            : AnalyteSubjectResolution.Empty;

        return new ExtractionResult(
            true, deduped, null, DocumentDate: documentDate, SuggestedTitle: suggestedTitle, Doctor: doctor,
            SpecimenResolution: specimenResolution, SubjectResolution: subjectResolution, RowCoverage: rowCoverage);
    }

    /// <summary>Итог извлечения по строкам-кандидатам (см. ExtractIndicatorsByRowsAsync) —
    /// UnmatchedRows содержит строки, которые так и не дали результата ни на основном, ни на
    /// повторном проходе (см. ExtractionRowCoverage).</summary>
    private sealed record RowExtractionOutcome(
        List<ExtractedLabIndicator> Indicators, IReadOnlyList<LabTableRow> UnmatchedRows,
        bool HadAnySuccessfulCall, bool HadAnyTransientFailure);

    /// <summary>Извлечение показателей по строкам-кандидатам (LabTableRowDetector) — вместо "дай
    /// кусок текста, найди в нём всё" модель получает точный пронумерованный список и обязана
    /// вернуть результат на каждую строку (см. AnalysisSystemPrompt). Строки идут батчами по
    /// RowBatchSize — один вызов на батч, а не на всю таблицу разом, чтобы не перегружать
    /// маленькую модель длинным списком сразу. После первого прохода строки, которые модель
    /// пропустила ИЛИ вернула с результатом, не прошедшим сверку (RowContainsValue), получают ОДИН
    /// повторный проход уже по одной строке — узкая задача, у которой заметно выше шанс успеха,
    /// чем у первого захода в составе батча.</summary>
    private async Task<RowExtractionOutcome> ExtractIndicatorsByRowsAsync(
        IReadOnlyList<LabTableRow> tableRows, string analysisPrompt,
        Action<Dictionary<string, JsonElement>> captureDocumentFields, CancellationToken ct)
    {
        var indicators = new List<ExtractedLabIndicator>();
        var hadAnySuccessfulCall = false;
        var hadAnyTransientFailure = false;
        var remaining = tableRows;

        for (var pass = 0; pass <= MaxRowRetryPasses && remaining.Count > 0; pass++)
        {
            var stillMissing = new List<LabTableRow>();
            var batchSize = pass == 0 ? RowBatchSize : 1;

            foreach (var batch in remaining.Chunk(batchSize))
            {
                var userText = BuildRowBatchUserText(batch);
                var result = await lmStudioClient.ExtractJsonAsync(analysisPrompt, userText, ct);
                if (!result.Success || result.Payload is null)
                {
                    if (result.IsTransient) hadAnyTransientFailure = true;
                    stillMissing.AddRange(batch);
                    continue;
                }
                hadAnySuccessfulCall = true;
                captureDocumentFields(result.Payload);

                var byRowId = batch.ToDictionary(r => r.RowId, r => r, StringComparer.Ordinal);
                var matchedInBatch = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (rowId, indicator) in ParseIndicators(result.Payload))
                {
                    // rowId не совпадает ни с одной строкой ЭТОГО батча — модель либо не
                    // проставила его (нарушила формат ответа), либо придумала лишний показатель;
                    // в обоих случаях без rowId нельзя сверить indicator.Value со "своей" строкой,
                    // безопаснее отбросить, чем гадать, к какой строке он относится.
                    if (rowId is null || !byRowId.TryGetValue(rowId, out var row)) continue;
                    if (matchedInBatch.Contains(rowId)) continue; // дубликат rowId — первый УСПЕШНЫЙ побеждает

                    // Антигаллюцинационная сверка: значение обязано встречаться в тексте СВОЕЙ
                    // строки (не всего чанка, как в старом гейте по имени) — точнее и устойчивее к
                    // переформулировке модели (снятая стрелка тренда, запятая вместо точки и т.п.,
                    // см. RowContainsValue). Строка отмечается "найденной" (matchedInBatch) ТОЛЬКО
                    // после этой проверки — иначе строка с забракованным значением молча считалась
                    // бы обработанной и не попадала бы в повторный проход ниже.
                    if (!RowContainsValue(row.RawLine, indicator.Value)) continue;
                    matchedInBatch.Add(rowId);
                    // Модель сокращает название ("MCV (ср. объем эритр.)" → "MCV") и по промпту "литературный регистр"
                    // портит аббревиатуры ("АЧТВ" → "Ачтв") — возвращаем полное название и написание из СВОЕЙ строки.
                    var blankName = FamilyHub.Infrastructure.Search.LabAnalyteNameCleaner.BlankNameWithoutValue(
                        row.Cells[row.NameCellIndex], indicator.Value);
                    var fullName = FamilyHub.Infrastructure.Search.LabAnalyteNameCleaner.PreferFullBlankName(indicator.Name, blankName, MaxIndicatorNameLength);
                    indicators.Add(indicator with { Name = FamilyHub.Infrastructure.Search.LabAnalyteNameCleaner.RestoreAbbreviations(fullName, blankName) });
                }

                stillMissing.AddRange(batch.Where(r => !matchedInBatch.Contains(r.RowId)));
            }

            remaining = stillMissing;
        }

        return new RowExtractionOutcome(indicators, remaining, hadAnySuccessfulCall, hadAnyTransientFailure);
    }

    private static string BuildRowBatchUserText(IReadOnlyList<LabTableRow> rows) =>
        string.Join('\n', rows.Select(r => $"[{r.RowId}] {r.RawLine}"));

    /// <summary>Сверка "значение действительно есть в своей строке" — по НОРМАЛИЗОВАННЫМ строкам
    /// (без пробелов, без стрелок тренда ↑/↓, запятая как разделитель дробной части приведена к
    /// точке, без учёта регистра), а не побайтово: модель может законно немного переформатировать
    /// значение — гейт должен пропускать такие случаи и ловить именно придуманное число/текст,
    /// которого в строке не было вовсе.</summary>
    private static bool RowContainsValue(string rawLine, string value)
    {
        var normalizedValue = NormalizeForContainmentCheck(value);
        return normalizedValue.Length > 0 &&
               NormalizeForContainmentCheck(rawLine).Contains(normalizedValue, StringComparison.Ordinal);
    }

    private static string NormalizeForContainmentCheck(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c is '↑' or '↓') continue;
            sb.Append(c == ',' ? '.' : char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d
            : null;

    private async Task<ExtractionResult> ExtractVisitAsync(DocumentContent content, CancellationToken ct)
    {
        var visitPrompt = await promptProvider.GetAsync("visit.extract", VisitSystemPrompt, ct);
        var hadAnySuccessfulCall = false;
        var hadAnyTransientFailure = false;

        if (content.Kind == DocumentSourceKind.Text)
        {
            foreach (var chunk in SplitIntoChunks(content.Text!, options.Value.MaxCharsPerChunk, ChunkOverlapChars))
            {
                var result = await lmStudioClient.ExtractJsonAsync(visitPrompt, chunk, ct);
                if (!result.Success || result.Payload is null)
                {
                    if (result.IsTransient) hadAnyTransientFailure = true;
                    continue;
                }
                hadAnySuccessfulCall = true;

                var conclusion = ParseConclusion(result.Payload);
                if (HasContent(conclusion))
                {
                    return new ExtractionResult(
                        true, null, conclusion,
                        DocumentDate: ParseDate(ReadString(result.Payload, "documentDate")),
                        SuggestedTitle: ReadString(result.Payload, "suggestedTitle"),
                        Doctor: ReadString(result.Payload, "doctor"));
                }
            }
        }
        else
        {
            foreach (var image in content.Images)
            {
                var result = await lmStudioClient.ExtractJsonAsync(
                    visitPrompt, "Распознай заключение врача на этом изображении.", [(image.Bytes, image.ContentType)], ct);
                if (!result.Success || result.Payload is null)
                {
                    if (result.IsTransient) hadAnyTransientFailure = true;
                    continue;
                }
                hadAnySuccessfulCall = true;

                var conclusion = ParseConclusion(result.Payload);
                if (HasContent(conclusion))
                {
                    return new ExtractionResult(
                        true, null, conclusion,
                        DocumentDate: ParseDate(ReadString(result.Payload, "documentDate")),
                        SuggestedTitle: ReadString(result.Payload, "suggestedTitle"),
                        Doctor: ReadString(result.Payload, "doctor"));
                }
            }
        }

        return new ExtractionResult(
            true, null, null, "Не удалось распознать заключение врача.",
            IsTransientFailure: hadAnyTransientFailure && !hadAnySuccessfulCall);
    }

    /// <summary>Плейсхолдеры "нет данных" — включает голый прочерк: в бланке он означает "поле не
    /// заполнено", а не результат анализа, хранить его незачем — график/тренд по нему всё равно
    /// не построить. Словесные "отсутствуют"/"не обнаружено"/"отрицательно" сюда НЕ входят — это
    /// настоящие качественные результаты.</summary>
    private static readonly HashSet<string> EmptyValuePlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "n/a", "na", "нет данных", "не указано", "неизвестно", "?", ".", "-", "—", "–",
    };

    /// <summary>RowId — эхо поля "rowId" из ответа модели (см. AnalysisSystemPrompt), null для
    /// вызовов без строк-кандидатов (vision-путь, fallback-чанки). Используется только вызывающим
    /// кодом строчного пути (ExtractIndicatorsByRowsAsync) для сверки с батчем — остальные пути
    /// его просто игнорируют.</summary>
    private static IEnumerable<(string? RowId, ExtractedLabIndicator Indicator)> ParseIndicators(
        Dictionary<string, JsonElement> payload)
    {
        if (!TryGetValue(payload, "indicators", out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var name = ReadString(item, "name")?.Trim();
            // Завершающая «*» — маркер «вне референса» из бланка («50.0*»), не часть значения: с ней число не
            // разбирается (ValueNumericText = null — нет тренда/графика), а флаг считается отдельно по референсу.
            var value = ReadString(item, "value")?.Trim().TrimEnd('*').TrimEnd();
            // Показатель без имени/значения, с неправдоподобно длинным именем (модель
            // сгенерировала предложение, не название показателя), или со значением-плейсхолдером
            // "нет данных" вместо реального пропуска ячейки — отбрасываем. Порог поднят с 80 до 160
            // (живой пример — протокол ГБУЗ РК, "Показатель" колонка печатает ПОЛНОЕ название
            // лабораторного теста, а не короткое имя: "Бактериальный микроорганизм, концентрация в
            // условных единицах в кале культуральным методом" — 90 символов, честно распознанное
            // имя с бланка, отбрасывалось прежним порогом целиком).
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value) || name.Length > MaxIndicatorNameLength) continue;
            if (EmptyValuePlaceholders.Contains(value)) continue;

            yield return (
                ReadString(item, "rowId")?.Trim(),
                new ExtractedLabIndicator(
                    Name: name,
                    Value: value,
                    Unit: ReadString(item, "unit"),
                    RefLow: ReadDouble(item, "refLow"),
                    RefHigh: ReadDouble(item, "refHigh"),
                    RefText: ReadString(item, "refText"),
                    RefExpected: ReadString(item, "refExpected")));
        }
    }

    private static VisitConclusion ParseConclusion(Dictionary<string, JsonElement> payload) => new(
        ReadString(payload, "diagnosis"),
        ReadString(payload, "recommendations"),
        ReadString(payload, "anamnesis"),
        ReadString(payload, "proceduresPerformed"),
        ParsePrescribedMedications(payload));

    private static List<PrescribedMedication> ParsePrescribedMedications(Dictionary<string, JsonElement> payload)
    {
        if (!TryGetValue(payload, "prescriptions", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];

        var result = new List<PrescribedMedication>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = ReadString(item, "name")?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 200) continue;
            result.Add(new PrescribedMedication(name, ReadString(item, "dosageInstructions")));
        }
        return result;
    }

    private static bool HasContent(VisitConclusion c) =>
        !string.IsNullOrWhiteSpace(c.Diagnosis) || !string.IsNullOrWhiteSpace(c.Recommendations) ||
        !string.IsNullOrWhiteSpace(c.Anamnesis) || !string.IsNullOrWhiteSpace(c.ProceduresPerformed) ||
        (c.PrescribedMedications is { Count: > 0 });

    /// <summary>Дедуп по (имя, значение, единица), НЕ по одному имени — иначе панель вида "МНО
    /// (+ПТВ и ПТИ)" (заголовок группы БЕЗ значения и отдельная строка показателя С тем же именем
    /// и своим значением, живой пример — бланк Гемотест) схлопнула бы вторую в первую и потеряла
    /// бы единственный реальный результат. Строк-кандидатов LabTableRowDetector с одинаковым
    /// (имя, значение, единица) в норме не бывает (каждая привязана к собственному rowId) — этот
    /// дедуп в первую очередь страхует vision-путь и fallback-чанки без строк-кандидатов, где то
    /// же самое значение может законно повториться на стыке двух чанков с перехлёстом
    /// (ChunkOverlapChars). Первое вхождение побеждает — куски идут по порядку документа.</summary>
    private static List<ExtractedLabIndicator> DeduplicateByName(List<ExtractedLabIndicator> indicators)
    {
        var seen = new HashSet<(string Name, string Value, string? Unit)>();
        var result = new List<ExtractedLabIndicator>();
        foreach (var indicator in indicators)
        {
            var key = (indicator.Name.Trim().ToLowerInvariant(), indicator.Value.Trim().ToLowerInvariant(),
                indicator.Unit?.Trim().ToLowerInvariant());
            if (seen.Add(key)) result.Add(indicator);
        }
        return result;
    }

    private static IEnumerable<string> SplitIntoChunks(string text, int maxChars, int overlap)
    {
        if (text.Length <= maxChars)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(maxChars, text.Length - start);
            yield return text.Substring(start, length);
            if (start + length >= text.Length) yield break;
            start += maxChars - overlap;
        }
    }
}
