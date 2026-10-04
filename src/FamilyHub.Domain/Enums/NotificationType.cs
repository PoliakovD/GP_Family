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
    /// таблица уведомлений не шифруется (ADR-0015). В Telegram и Web Push Title/Body не уходят вовсе —
    /// только обобщённый текст по типу (TelegramOutboundPublisher.BuildGenericText).</summary>
    MedicationDoseDue = 10,

    /// <summary>Приём лекарства не отмечен вовремя — адресуется наблюдателям за курсом.</summary>
    MedicationDoseMissed = 11,

    /// <summary>Остатка в аптечке хватит на несколько дней курса или меньше.</summary>
    MedicationStockLow = 12,

    /// <summary>Срок прививки по календарю приближается (за 2 недели). Текст намеренно без названия
    /// инфекции/вакцины — по той же причине, что у MedicationDoseDue (ADR-0004/ADR-0016).</summary>
    VaccinationDue = 13,

    /// <summary>Срок прививки прошёл, но это не страшно — можно сделать. Одно уведомление, не повтор.</summary>
    VaccinationOverdue = 14,

    /// <summary>Напоминание о самочувствии через N дней после прививки (только для своих).</summary>
    VaccinationWellbeingCheck = 15,

    /// <summary>Член семьи составил отчёт для врача о пользователе. Текст без медицинских подробностей.</summary>
    DoctorReportAboutYou = 16,
}
