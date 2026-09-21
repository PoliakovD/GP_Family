using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — применение миграций при старте с retry
/// для транзиентных ошибок (race-condition нескольких реплик), без изменения поведения/порядка.
/// </summary>
public static class MigrationRunner
{
    public static async Task MigrateDatabaseWithRetryAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                break;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning(ex,
                    "Применение миграций не удалось (попытка {Attempt}/{Max}), повтор через {Delay}s",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay);
            }
        }
    }
}
