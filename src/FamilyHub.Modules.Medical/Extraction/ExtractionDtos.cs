namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Один показатель, как его вернула модель — сырой выход LLM ДО нормализации/привязки к
/// справочнику/сохранения (ветка medicalrecords, реализация LmStudioMedicalDocumentExtractor).
/// Названа не LabIndicator намеренно: так называется персистентная сущность
/// (FamilyHub.Domain.Entities.LabIndicator) — коллизия имён в разных неймспейсах компилируется,
/// но путает при чтении кода конвейера (см. MedicalDocumentExtractionProcessor, который
/// превращает ExtractedLabIndicator[] в LabIndicator[]). RefLow/RefHigh — границы референсного
/// диапазона, если распознаны в самом бланке.
/// RefText — референс как напечатан целиком, когда он не раскладывается на
/// RefLow/RefHigh ("отрицательно", "1-3 в п/зр", "норма"). RefLow/RefHigh заполняются только
/// когда референс — числовой диапазон.</summary>
public record ExtractedLabIndicator(string Name, string Value, string? Unit, double? RefLow, double? RefHigh, string? RefText);

/// <summary>Один назначенный препарат из заключения врача (UX-редизайн) — DosageInstructions как
/// написано в документе ("по 1 таблетке 2 раза в день после еды"), не структурировано дальше.
/// Ссылка на общий справочник (KbMedicationId) НЕ хранится здесь — резолвится на чтение
/// (см. ExtractionQueryService.GetConclusionAsync), чтобы не требовать бэкофилла старых записей,
/// когда обогащение справочника завершится позже первого просмотра.</summary>
public record PrescribedMedication(string Name, string? DosageInstructions);

/// <summary>Заключение врача из распознанного документа (задача 5.3 + UX-редизайн). Anamnesis —
/// анамнез (жалобы, история болезни со слов пациента); ProceduresPerformed — проведённые на
/// приёме манипуляции/анализы (не путать с LabIndicator — это текст заключения, не структурные
/// показатели); PrescribedMedications — назначенные препараты с дозировкой, каждый по возможности
/// связывается со справочником kb.global_medications_kb.</summary>
public record VisitConclusion(
    string? Diagnosis,
    string? Recommendations,
    string? Anamnesis,
    string? ProceduresPerformed,
    IReadOnlyList<PrescribedMedication>? PrescribedMedications);

/// <summary>
/// Результат распознавания одного вложения. Ровно одно из <see cref="LabIndicators"/>/
/// <see cref="Conclusion"/> заполнено при Supported — по
/// <see cref="FamilyHub.Domain.Enums.MedicalRecordKind"/> исходной записи.
///
/// v2: поля уровня ДОКУМЕНТА (не индикатора) — распознаются один раз на вложение, дешевле для
/// модели, чем спрашивать это же на каждый показатель:
/// <see cref="DocumentDate"/> — дата анализа/приёма, напечатанная в бланке, если распозналась —
/// процессор обновляет MedicalRecord.RecordDate (по умолчанию — дата создания записи).
/// <see cref="SuggestedTitle"/> — короткое название документа ("Общий анализ крови"), если оно
/// прямо напечатано в шапке бланка — процессор пишет в MedicalRecord.Title, если оно ещё пустое.
/// <see cref="Doctor"/> — врач/специалист, если указан в документе (для анализа — "кто назначил",
/// для визита — принимавший врач) — процессор пишет в MedicalRecord.Doctor, если оно ещё пустое
/// (не затирает то, что пользователь мог ввести вручную в форме создания).
///
/// Источник показателя (биоматериал/исследование) больше НЕ структурное поле этого результата —
/// определяется отдельным проходом SpecimenResolver по документу целиком (один вызов LLM на файл,
/// не побочное поле промпта структурирования показателей — совмещение задач мешало обеим), сырой
/// (ещё не сведённый к строке справочника) итог этого прохода лежит в <see cref="SpecimenResolution"/>.
/// </summary>
public record ExtractionResult(
    bool Supported,
    IReadOnlyList<ExtractedLabIndicator>? LabIndicators,
    VisitConclusion? Conclusion,
    string? FailureReason = null,
    DateOnly? DocumentDate = null,
    string? SuggestedTitle = null,
    string? Doctor = null,
    SpecimenDocumentResolution? SpecimenResolution = null);
