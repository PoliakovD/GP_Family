using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Доступ на чтение к части моих личных медицинских данных, выданный одному члену семьи (ADR-0017) —
/// «взрослый ребёнок наблюдает за пожилым родителем» и подобные сценарии. UNIQUE(OwnerUserId,
/// ViewerUserId): максимум одна строка на пару, набор категорий — битовая маска
/// <see cref="HealthShareCategory"/>. Строка с Categories == None не хранится — её удаляют
/// (см. HealthShareService.SetAsync), а не оставляют пустой.
///
/// В отличие от FamilyMedicalShare (доступ на СЕМЬЮ целиком, только для анализов/визитов), грант —
/// per-person и действует, только пока у владельца и зрителя есть общая активная семья: проверка —
/// в момент запроса (см. SubjectScopeService), не через явный revoke при выходе из семьи.
/// </summary>
public class HealthShareGrant
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid ViewerUserId { get; set; }

    public HealthShareCategory Categories { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
