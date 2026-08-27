using System.Reflection;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Consents;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Consents;

public enum AcceptConsentResult { Accepted, StaleVersion }

/// <summary>
/// Согласия на обработку ПДн (задача 2.3): версионирование текста, идемпотентное принятие,
/// статус для фронтенд-гейта. Тексты — embedded-ресурсы сборки (история версий — git).
/// </summary>
public class ConsentService(AppDbContext db, IMemoryCache cache, IOptions<ConsentOptions> options)
{
    public string CurrentVersion => options.Value.CurrentVersion;

    public static string LoadLegalText(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fullName = $"FamilyHub.Api.Legal.{resourceName}";
        using var stream = assembly.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException($"Не найден embedded-ресурс {fullName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public Task<bool> HasAcceptedCurrentAsync(Guid userId, CancellationToken ct = default) =>
        db.Set<UserConsent>().AnyAsync(
            c => c.UserId == userId && c.Kind == ConsentKind.PdnConsent && c.Version == CurrentVersion, ct);

    public async Task<AcceptConsentResult> AcceptAsync(Guid userId, string version, CancellationToken ct = default)
    {
        // Пользователь подтверждает ту версию, которую видел: расхождение с актуальной —
        // признак устаревшего клиента, согласие не засчитывается.
        if (version != CurrentVersion) return AcceptConsentResult.StaleVersion;

        // Два обязательных чекбокса на ConsentGateComponent (общий + отдельный на спецкатегорию
        // здоровья, ч. 2 ст. 10 152-ФЗ) гейтят одну кнопку «Принять» — оба были отмечены к
        // моменту вызова, поэтому здесь пишем обе строки атомарно одним запросом. Каждая — своя
        // идемпотентность (UNIQUE(UserId, Kind, Version)), т.к. это независимые записи.
        await AddIfMissingAsync(userId, ConsentKind.PdnConsent, version, ct);
        await AddIfMissingAsync(userId, ConsentKind.SpecialCategoryConsent, version, ct);

        // Прогрев кэша ConsentRequiredFilter: принятие видно немедленно.
        cache.Set(ConsentRequiredFilter.CacheKey(userId, version), true, TimeSpan.FromMinutes(5));
        return AcceptConsentResult.Accepted;
    }

    private async Task AddIfMissingAsync(Guid userId, ConsentKind kind, string version, CancellationToken ct)
    {
        var exists = await db.Set<UserConsent>().AnyAsync(
            c => c.UserId == userId && c.Kind == kind && c.Version == version, ct);
        if (exists) return;

        var consent = new UserConsent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Kind = kind,
            Version = version,
            AcceptedAt = DateTime.UtcNow,
        };
        db.Set<UserConsent>().Add(consent);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Гонка двойного клика: UNIQUE(UserId, Kind, Version) — согласие уже записано.
            // Детач только своей записи (аудит, находка Medium #5), не ChangeTracker.Clear() —
            // тот сбрасывал бы ВСЕ отслеживаемые изменения на разделяемом scoped AppDbContext,
            // включая несвязанные сущности, которые мог успеть затрекать тот же запрос (тот же
            // риск, из-за которого NotificationSendingService.AddIfNewAsync намеренно детачит
            // только свою сущность — см. её комментарий).
            db.Entry(consent).State = EntityState.Detached;
        }
    }
}
