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

    /// <summary>Распознавание вложения окончательно не удалось (ветка medicalrecords) — публикуется
    /// только на настоящий терминальный отказ, не на техническую недоступность LM Studio
    /// (та резюмируется молча, см. LmStudioRecoverySweepJob, а сообщать "не удалось" в этот момент
    /// было бы дезинформацией).</summary>
    MedicalDocumentExtractionFailed = 8,

    /// <summary>Обогащение справочника препаратом окончательно не удалось (этап 4) — тот же
    /// принцип, что MedicalDocumentExtractionFailed: не на технический сбой LM Studio.</summary>
    MedicationEnrichmentFailed = 9,

    /// <summary>Пора принять лекарство (курс приёма). Текст намеренно без названия препарата:
    /// таблица уведомлений не шифруется, а Telegram пересылает текст дословно (ADR-0015).</summary>
    MedicationDoseDue = 10,

    /// <summary>Приём лекарства не отмечен вовремя — адресуется наблюдателям за курсом.</summary>
    MedicationDoseMissed = 11,

    /// <summary>Остатка в аптечке хватит на несколько дней курса или меньше.</summary>
    MedicationStockLow = 12,
}
