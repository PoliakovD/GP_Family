namespace FamilyHub.Domain.Entities;

/// <summary>
/// Персональный результат анализа совместимости препаратов (задача 2.6, логика — этап 5.1).
/// В отличие от GlobalMedicationKb жёстко привязан к пользователю и НИКОГДА не шарится —
/// даже членам семьи: набор принимаемых препаратов сам по себе чувствительная информация.
/// </summary>
public class PersonalCompatibilityResult
{
    public Guid Id { get; set; }

    /// <summary>Владелец результата — единственный, кому он виден.</summary>
    public Guid UserId { get; set; }

    /// <summary>Хеш нормализованного набора входных препаратов — кэш-ключ повторного анализа.</summary>
    public string InputHash { get; set; } = string.Empty;

    /// <summary>Результат анализа (jsonb).</summary>
    public string ResultJson { get; set; } = "{}";

    /// <summary>Версия модели/конвейера, породившей результат.</summary>
    public string ModelVersion { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
