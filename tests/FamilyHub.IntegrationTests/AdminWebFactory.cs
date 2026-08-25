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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("Admin:Enabled", "true");
        builder.UseSetting("Admin:User", TestUser);
        builder.UseSetting("Admin:Password", TestPassword);
        builder.UseSetting("Admin:SessionLifetime", "00:00:05"); // короткий TTL — тест истечения без Thread.Sleep на часы
    }
}
