namespace FamilyHub.Domain.Enums;

/// <summary>Виды оповещений: от фоновой джобы (этап 3 п.10 брифа) и доменных событий (этап 1 плана).</summary>
public enum NotificationType
{
    /// <summary>Срок годности лекарства приближается (в пределах окна предупреждения).</summary>
    MedicationExpiringSoon = 0,

    /// <summary>Срок годности лекарства уже истёк.</summary>
    MedicationExpired = 1,

    /// <summary>День рождения наступает в пределах окна предупреждения.</summary>
    BirthdayUpcoming = 2,

    /// <summary>Участник покинул семью (сам или выгнан) — адресуется админам семьи.</summary>
    MemberLeft = 3,

    /// <summary>Заявка на вступление одобрена — адресуется остальным членам семьи.</summary>
    MemberApproved = 4,

    /// <summary>Участник открыл семье доступ к своим медицинским записям.</summary>
    MedicalRecordShared = 5,

    /// <summary>Справочник пополнен данными о препарате, сохранённом пользователем (этап 4).</summary>
    MedicationEnriched = 6,

    /// <summary>Распознавание вложения (анализ/выписка врача) завершено (ветка medicalrecords).</summary>
    MedicalDocumentExtracted = 7,
}
