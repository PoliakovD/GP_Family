namespace FamilyHub.Domain.Enums;

/// <summary>
/// Статус проверки записи справочника человеком (ADR-0018) — ВНУТРЕННИЙ маркер для админа: нигде не
/// отдаётся пользовательским API/DTO и не показывается в пользовательском фронте (см. тест
/// KbVerificationNotExposedTests). Значения хранятся числом в kb.global_*_kb.VerificationStatus.
/// </summary>
public enum KbVerificationStatus
{
    /// <summary>Записано автоматикой (ИИ + веб-поиск), человек не смотрел. Значение по умолчанию.</summary>
    AiUnverified = 0,

    /// <summary>Админ проверил и одобрил как есть (одобрение черновика без правок или «Отметить проверенным»).</summary>
    AdminVerified = 1,

    /// <summary>Админ поправил запись вручную (одобрение с правками либо правка в каталоге).</summary>
    AdminEdited = 2,

    /// <summary>Запись построена на знании эксперта (ручные сниппеты без веб-источников), а не на веб-поиске.</summary>
    ManualKnowledge = 3,
}
