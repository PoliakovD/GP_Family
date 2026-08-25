namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Один показатель, как его вернула модель — сырой выход LLM ДО нормализации/привязки к
/// справочнику/сохранения (ветка medicalrecords, реализация LmStudioMedicalDocumentExtractor).
/// Названа не LabIndicator намеренно: так называется персистентная сущность
/// (FamilyHub.Domain.Entities.LabIndicator) — коллизия имён в разных неймспейсах компилируется,
/// но путает при чтении кода конвейера (см. MedicalDocumentExtractionProcessor, который
/// превращает ExtractedLabIndicator[] в LabIndicator[]). RefLow/RefHigh — границы референсного
/// диапазона, если распознаны в самом бланке.</summary>
/// <summary>RefText — референс как напечатан целиком, когда он не раскладывается на
/// RefLow/RefHigh ("отрицательно", "1-3 в п/зр", "норма"). RefLow/RefHigh заполняются только
/// когда референс — числовой диапазон.</summary>
public record ExtractedLabIndicator(string Name, string Value, string? Unit, double? RefLow, double? RefHigh, string? RefText);

/// <summary>Заключение врача из распознанного документа (задача 5.3: только извлечение — график
/// приёма → календарь → push вне объёма этой ветки, см. план). Prescriptions — сырой текст
/// назначений.</summary>
public record VisitConclusion(string? Diagnosis, string? Recommendations, string? Prescriptions);

/// <summary>
/// Результат распознавания одного вложения. Ровно одно из <see cref="LabIndicators"/>/
/// <see cref="Conclusion"/> заполнено при Supported — по
/// <see cref="FamilyHub.Domain.Enums.MedicalRecordKind"/> исходной записи.
/// </summary>
public record ExtractionResult(bool Supported, IReadOnlyList<ExtractedLabIndicator>? LabIndicators, VisitConclusion? Conclusion, string? FailureReason = null);
