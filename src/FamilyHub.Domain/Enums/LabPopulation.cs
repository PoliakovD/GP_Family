namespace FamilyHub.Domain.Enums;

/// <summary>
/// Категория популяции, к которой применим референсный диапазон (ветка medicalrecords, редизайн
/// enrich-пайплайна) — систематизированный словарь для UX-бейджей. Возраст/пол сами по себе
/// покрываются AgeFrom/AgeTo/Sex на LabAnalyteReferenceRange — эта категория для случаев, где
/// применимость нормы зависит не только от них (беременность, фаза цикла).
/// IndicatorFlagCalculator автоматически сравнивает значение только для General и Children — для
/// Pregnancy/CyclePhase в домене нет сигнала, чтобы выбрать нужную строку безопасно, поэтому такие
/// диапазоны только показываются в статье справочника, не участвуют в автоматическом флаге.
/// </summary>
public enum LabPopulation
{
    General = 0,
    Pregnancy = 1,
    Children = 2,
    CyclePhase = 3,
}
