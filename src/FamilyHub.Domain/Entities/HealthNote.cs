using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Запись личного дневника самочувствия — строго персональный ресурс: видна только владельцу,
/// не шарится с семьёй и не бывает «за подопечного». НЕ реализует IFamilyOwned.
/// Всё, что описывает состояние человека (название симптома, значения замеров, текст), —
/// [Encrypted] (спецкатегория ПДн, ADR-0002), поэтому SQL фильтрует только по владельцу, виду и
/// времени, а остальное — в памяти после расшифровки (объёмы на одного пользователя малы).
/// Payload по виду лежит в <see cref="DataJson"/> (см. HealthNotes/HealthNotePayloads).
/// </summary>
public class HealthNote
{
    public Guid Id { get; set; }

    /// <summary>Владелец. FK на User с CASCADE — при удалении аккаунта дневник уходит вместе с ним.</summary>
    public Guid OwnerUserId { get; set; }

    public HealthNoteKind Kind { get; set; }

    /// <summary>Когда это произошло (для сна — момент пробуждения), а не когда внесли запись.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>Название: симптом («Головная боль») или препарат («Сорбифер 100 мг»). У остальных
    /// видов null — замер определяется кодом в payload'е.</summary>
    [Encrypted]
    public string? Title { get; set; }

    /// <summary>Типизированный payload по виду записи, сериализованный JSON. [Encrypted] ⇒ строка,
    /// не jsonb: фильтрация по содержимому в SQL невозможна по построению.</summary>
    [Encrypted]
    public string? DataJson { get; set; }

    /// <summary>Свободный текст; необязателен у всех видов, кроме Note.</summary>
    [Encrypted]
    public string? Text { get; set; }

    /// <summary>«Включить в вопросы к врачу»: текст попадает в блок жалоб следующего отчёта.
    /// Не шифруется — булев флаг ничего не раскрывает, а нужен для выборки в SQL.</summary>
    public bool IncludeInDoctorQuestions { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
