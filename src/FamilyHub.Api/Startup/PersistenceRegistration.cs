using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — БД-контекст + Data Protection, без
/// изменения поведения/порядка.
/// </summary>
public static class PersistenceRegistration
{
    /// <returns>Строка подключения Postgres — раньше читалась (тем же самым guard'ом) ЕЩЁ РАЗ в
    /// AddFamilyHubBackgroundJobs (Hangfire storage), при cleanup-рефакторинге сведено к одному
    /// чтению здесь, значение передаётся дальше явным параметром (см. Program.cs).</returns>
    public static (WebApplicationBuilder Builder, string PostgresConnectionString) AddFamilyHubPersistence(
        this WebApplicationBuilder builder)
    {
        // Стеммер/триграммы — чистые функции без состояния (этап 3, ADR-0003): singleton безопасен.
        // Общий для Modules.Medical (медкарты) и Modules.Birthdays (дни рождения) — оба зависят только
        // от Domain/Infrastructure и не ссылаются друг на друга, поэтому регистрация — здесь, не в модуле.
        builder.Services.AddSingleton<IRussianTextSearcher, RussianTextSearcher>();

        // --- Persistence ---
        var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Postgres.");
        builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(postgresConnectionString));

        // --- Data Protection (отладка 2026-08-20): без этого ключи живут в эфемерной ФС контейнера ---
        // (~/.aspnet/DataProtection-Keys) — каждый перезапуск/редеплой api сбрасывает их, инвалидируя
        // CSRF-токены (IAntiforgery, единственный потребитель Data Protection в этом приложении — JWT
        // подписывается отдельным Jwt:SigningKey, не затронут) у всех активных сессий.
        // PersistKeysToDbContext — та же Postgres, что и остальное состояние, автоматически попадает
        // под уже настроенный ночной pg_dump (см. deploy/backup).
        builder.Services.AddDataProtection()
            .SetApplicationName("FamilyHub")
            .PersistKeysToDbContext<AppDbContext>();

        return (builder, postgresConnectionString);
    }
}
