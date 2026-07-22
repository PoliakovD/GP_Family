using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Security;

/// <summary>
/// Проверяет, что [Encrypted]-поля реально лежат в БД шифротекстом (raw SQL в обход
/// конвертера), а через EF читаются открытым текстом.
/// </summary>
public class EncryptedFieldsPersistenceTests : SqliteTestBase
{
    [Fact]
    public async Task MedicalRecordPersonName_IsCiphertextInDb_PlaintextViaEf()
    {
        var owner = Db.AddUser();
        var record = TestData.NewMedicalRecord(owner.Id);
        record.PersonName = "Иванов Иван";
        record.Doctor = "Доктор Айболит";
        Db.MedicalRecords.Add(record);
        await Db.SaveChangesAsync();

        // Таблица в тестовой БД содержит единственную строку — WHERE по Guid не нужен
        // (формат хранения Guid в SQLite — деталь провайдера).
        var raw = await ReadRawAsync("SELECT PersonName || '|' || Doctor FROM MedicalRecords");
        raw.Should().NotContain("Иванов", "в БД должен лежать шифротекст");
        raw.Should().NotContain("Айболит");
        raw.Should().Contain("enc:v1:");

        var viaEf = await NewContext().MedicalRecords.AsNoTracking().SingleAsync(r => r.Id == record.Id);
        viaEf.PersonName.Should().Be("Иванов Иван");
        viaEf.Doctor.Should().Be("Доктор Айболит");
    }

    [Fact]
    public async Task BirthdayPersonName_IsCiphertextInDb()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var birthday = TestData.NewBirthday(family.Id, new DateOnly(1990, 5, 1));
        birthday.PersonName = "Бабушка Оля";
        Db.Birthdays.Add(birthday);
        await Db.SaveChangesAsync();

        var raw = await ReadRawAsync("SELECT PersonName FROM Birthdays");
        raw.Should().NotContain("Оля");
        raw.Should().Contain("enc:v1:");
    }

    private async Task<string> ReadRawAsync(string sql)
    {
        var connection = (SqliteConnection)Db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
