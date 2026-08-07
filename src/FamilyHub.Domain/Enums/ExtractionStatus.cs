namespace FamilyHub.Domain.Enums;

/// <summary>
/// Статус распознавания вложенного скана (OCR-конвейер, задачи 5.2/5.3 — пока не реализован,
/// см. .claude/plans/medical-platform/stage/stage-5). None — распознавание не запрашивалось.
/// </summary>
public enum ExtractionStatus
{
    None = 0,
    Pending = 1,
    Ready = 2,
    Failed = 3,
}
