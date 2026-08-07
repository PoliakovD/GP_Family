namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Один показатель из бланка анализа (задача 5.2 — не реализована, см.
/// .claude/plans/medical-platform/stage/stage-5/task-5.2-lab-results.md). RefLow/RefHigh — границы
/// референсного диапазона, если распознаны; подсветка отклонений строится по ним на фронте.</summary>
public record LabIndicator(string Name, string Value, string? Unit, double? RefLow, double? RefHigh);

/// <summary>Заключение врача из распознанного документа (задача 5.3 — не реализована, см.
/// .claude/plans/medical-platform/stage/stage-5/task-5.3-doctors-prescriptions.md). Prescriptions —
/// сырой текст назначений; извлечение структурированного графика приёма в отдельную задачу.</summary>
public record VisitConclusion(string? Diagnosis, string? Recommendations, string? Prescriptions);

/// <summary>
/// Результат распознавания одного вложения. Ровно одно из <see cref="LabIndicators"/>/
/// <see cref="Conclusion"/> заполнено — по <see cref="FamilyHub.Domain.Enums.MedicalRecordKind"/>
/// исходной записи. Сериализуется в MedicalRecord.ExtractedDataJson.
/// </summary>
public record ExtractionResult(bool Supported, IReadOnlyList<LabIndicator>? LabIndicators, VisitConclusion? Conclusion);
