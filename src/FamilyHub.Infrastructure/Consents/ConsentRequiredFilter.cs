using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Consents;

/// <summary>
/// Жёсткая серверная гарантия задачи 2.3: обработка медданных (модули Medical и Birthdays)
/// доступна только принявшим актуальную версию согласия ПДн. Фронтенд-гейт — первичный UX,
/// фильтр — контроль на случай прямых вызовов API. Анонимные запросы пропускает (их
/// отсекает FallbackPolicy или защищает подписанный токен, как у скачивания вложений).
/// </summary>
public class ConsentRequiredFilter(
    AppDbContext db,
    IMemoryCache cache,
    IOptions<ConsentOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var userId = context.HttpContext.User.GetUserId();
        if (userId is null) return await next(context);

        var version = options.Value.CurrentVersion;
        var cacheKey = CacheKey(userId.Value, version);

        if (!cache.TryGetValue(cacheKey, out _))
        {
            var accepted = await db.Set<Domain.Entities.UserConsent>().AnyAsync(
                c => c.UserId == userId && c.Kind == ConsentKind.PdnConsent && c.Version == version,
                context.HttpContext.RequestAborted);

            if (!accepted)
                return Results.Json(new { code = "consent_required", version }, statusCode: StatusCodes.Status403Forbidden);

            // Кэшируем только положительный ответ: принятие согласия видно сразу.
            cache.Set(cacheKey, true, TimeSpan.FromMinutes(5));
        }

        return await next(context);
    }

    /// <summary>Общий ключ кэша — ConsentService прогревает его при принятии согласия.</summary>
    public static string CacheKey(Guid userId, string version) => $"consent:{userId}:{version}";
}
