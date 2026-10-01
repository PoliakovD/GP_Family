using FamilyHub.Api.Features.Admin;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Платный поиск обогащения теперь ждёт ручного одобрения админа (ADR-0018) — интеграционные тесты, которые гоняют
/// конвейер с реальным (не Null) провайдером, должны сыграть роль админа: дождаться парковки задачи в
/// AwaitingSearchApproval и одобрить её тем же сервисом, что стоит за очередью «Одобрение».
/// </summary>
public static class ReviewTestHelper
{
    /// <summary>Ждёт, пока задача обогащения препарата с этим NormalizedName припаркуется на одобрении поиска, и одобряет.</summary>
    public static async Task ApproveMedicationSearchAsync(
        IServiceProvider services, string normalizedName, string? queryText = null, int timeoutMs = 45_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.MedicationEnrichmentJobs.AsNoTracking().FirstOrDefaultAsync(
                j => j.NormalizedName == normalizedName && j.Status == EnrichmentJobStatus.AwaitingSearchApproval);
            if (job is not null)
            {
                var review = scope.ServiceProvider.GetRequiredService<AdminEnrichmentReviewService>();
                var outcome = await review.ApproveSearchAsync(
                    ReviewKinds.Medication, job.Id, new ApproveSearchRequest(queryText));
                outcome.Result.Should().Be(ReviewActionResult.Ok, outcome.Message);
                return;
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"Задача «{normalizedName}» не припарковалась на одобрении платного поиска за {timeoutMs} мс.");
    }

    /// <summary>Ждёт, пока задача препарата достигнет нужного статуса (ожидание гейтов очереди).</summary>
    public static async Task<Guid> WaitForMedicationJobAsync(
        IServiceProvider services, string normalizedName, EnrichmentJobStatus status, int timeoutMs = 45_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.MedicationEnrichmentJobs.AsNoTracking()
                .Where(j => j.NormalizedName == normalizedName && j.Status == status)
                .OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync();
            if (job is not null) return job.Id;
            await Task.Delay(300);
        }

        throw new TimeoutException($"Задача «{normalizedName}» не достигла статуса {status} за {timeoutMs} мс.");
    }
}
