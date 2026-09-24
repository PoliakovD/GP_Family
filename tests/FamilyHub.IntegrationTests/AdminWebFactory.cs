using Microsoft.AspNetCore.Hosting;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// FamilyHubWebFactory + Admin:Enabled=true (ADR-0009) — единственная фабрика с включённой
/// /api/admin/*, тем же приёмом, что KafkaWebFactory включает Messaging:Kafka:Enabled=true.
/// Остальные интеграционные тесты нарочно бегут с Admin:Enabled=false (по умолчанию в
/// FamilyHubWebFactory) — доказывает, что без явного включения поверхность недостижима вообще
/// (ни схема AuthSchemes.Admin не регистрируется, ни /api/admin/* не примаплен, см. Program.cs).
/// </summary>
public class AdminWebFactory : FamilyHubWebFactory
{
    public const string TestUser = "admin-test-user";
    public const string TestPassword = "admin-test-password";

    /// <summary>Заведомо узнаваемое значение секрета Enrichment:ApiKey — AdminConfigApiTests
    /// проверяет, что оно ни при каких условиях не появляется в ответе /api/admin/config.</summary>
    public const string EnrichmentApiKeySentinel = "sentinel-enrichment-api-key-DO-NOT-LEAK";

    /// <summary>Один отставной ключ ссылок на скачивание — AdminStatsApiTests проверяет, что
    /// «Ключи» показывают их реальное число (раньше счётчик был захардкожен нулём).</summary>
    public const string PreviousDownloadKey = "test-previous-download-signing-key";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("Admin:Enabled", "true");
        builder.UseSetting("Admin:User", TestUser);
        builder.UseSetting("Admin:Password", TestPassword);
        // Короче прод-дефолта (12 часов, AdminOptions.SessionLifetime), но заведомо длиннее любого
        // поллинга в этой коллекции (LabAnalyteKbRebuildJobTests.WaitForAsync — до 45с) — раньше
        // здесь стояло 5с "для теста истечения без ожидания часами", но ни один тест в коллекции
        // реального ожидания истечения не проверяет, а короткий TTL истекал СРЕДИ долгого поллинга
        // и валил его 401-м (сессия — общая на всю AdminWebFactory, не только на гипотетический
        // тест истечения). Понадобится тест именно истечения — заводить для него отдельную фабрику
        // с ещё более коротким TTL, не трогая этот дефолт.
        builder.UseSetting("Admin:SessionLifetime", "00:02:00");

        builder.UseSetting("Enrichment:ApiKey", EnrichmentApiKeySentinel);
        builder.UseSetting("Attachments:PreviousSigningKeys:0", PreviousDownloadKey);
    }
}
