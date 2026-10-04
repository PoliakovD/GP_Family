using FamilyHub.Domain.Entities;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Разбивка показателей записи на подряд идущие блоки по разделу бланка (LabIndicator.PanelLabel) —
/// для входа LLM-сводок (LabSummarizer/ClinicianLabSummarizer): модель видит, что "Нейтрофилы" —
/// часть "Лейкоцитарной формулы", а не отдельный заказ. Раздел — только структура входа, промпты
/// сводок от него не зависят. Без единого раздела у записи (старые записи, фото без заголовков) —
/// один блок без заголовка в исходном порядке списка, т.е. вход сводки ровно как раньше.
/// </summary>
public static class LabPanelGrouping
{
    /// <summary>Заголовок блока показателей без раздела, идущего после блока с разделом.</summary>
    public const string NoPanelHeader = "Прочие показатели";

    public sealed record Block(string? Header, IReadOnlyList<LabIndicator> Indicators);

    public static IReadOnlyList<Block> SplitByPanel(IReadOnlyList<LabIndicator> indicators)
    {
        if (!indicators.Any(i => i.PanelLabel is not null)) return [new Block(null, indicators)];

        var blocks = new List<Block>();
        string? currentPanel = null;
        List<LabIndicator>? current = null;
        // Порядок бланка (Position) — блоки раздела на бланке идут подряд.
        foreach (var indicator in indicators.OrderBy(i => i.Position))
        {
            if (current is null || !string.Equals(indicator.PanelLabel, currentPanel, StringComparison.Ordinal))
            {
                currentPanel = indicator.PanelLabel;
                current = [];
                // Блок без раздела в самом начале — без заголовка; после блока с разделом — явный
                // заголовок, иначе модель отнесла бы эти показатели к предыдущему разделу.
                blocks.Add(new Block(currentPanel ?? (blocks.Count == 0 ? null : NoPanelHeader), current));
            }
            current.Add(indicator);
        }
        return blocks;
    }
}
