namespace FamilyHub.Domain.Entities;

/// <summary>
/// "Недавно использованные этим пользователем" источники показателя (пересборка enrich-пайплайна) —
/// тонкая таблица поверх общего <see cref="GlobalSpecimenKb"/>, а НЕ второй источник истины
/// написания (тот один — GlobalSpecimenKb.DisplayName). Раньше (до пересборки) хранила свой
/// NormalizedName/DisplayName как персональная копия провалидированного биоматериала вне enum —
/// теперь, когда источник целиком в общем справочнике, роль этой таблицы сузилась до "что
/// автоподсказка должна предложить В ПЕРВУЮ ОЧЕРЕДЬ конкретному человеку" (см. UserSpecimenService).
/// </summary>
public class UserSpecimen
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    /// <summary>Ссылка (не FK — GlobalSpecimenKb в отдельной схеме kb) на использованную запись
    /// общего справочника.</summary>
    public Guid SpecimenKbId { get; set; }

    /// <summary>Последнее использование — автоподсказка сортирует по этому полю, не по дате
    /// первого добавления.</summary>
    public DateTime LastUsedAt { get; set; }
}
