using System.Data.Common;
using FamilyHub.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Acceptance 2.2: схемы identity (ПДн) и medical (обезличенное) физически разделены.
/// Проверяем фактическое размещение таблиц по pg_tables после реальных миграций.
/// </summary>
public class SchemaSeparationTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    [Theory]
    [InlineData("Users", "identity")]
    [InlineData("Families", "identity")]
    [InlineData("FamilyMembers", "identity")]
    [InlineData("Notifications", "identity")]
    [InlineData("Birthdays", "identity")]
    [InlineData("MedicalRecords", "medical")]
    [InlineData("FamilyMedicalShares", "medical")]
    [InlineData("FileAttachments", "medical")]
    [InlineData("Medkits", "medical")]
    [InlineData("Medications", "medical")]
    [InlineData("OutboxMessage", "public")]
    [InlineData("OutboxState", "public")]
    [InlineData("InboxState", "public")]
    public async Task Table_LivesInExpectedSchema(string table, string expectedSchema)
    {
        // Прогрев: первый запрос гарантирует, что хост поднят и миграции применены.
        (await ClientAs(FreshTelegramId()).GetAsync("/api/families")).EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT schemaname FROM pg_tables WHERE tablename = @table";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@table";
        parameter.Value = table;
        command.Parameters.Add(parameter);

        (await command.ExecuteScalarAsync()).Should().Be(expectedSchema);
    }
}
