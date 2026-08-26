using FamilyHub.Domain.Entities;
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
    public async Task MedicalRecordTitle_IsCiphertextInDb_PlaintextViaEf()
    {
        var owner = Db.AddUser();
        var record = TestData.NewMedicalRecord(owner.Id);
        record.Title = "Общий анализ крови";
        record.Doctor = "Доктор Айболит";
        Db.MedicalRecords.Add(record);
        await Db.SaveChangesAsync();

        // Таблица в тестовой БД содержит единственную строку — WHERE по Guid не нужен
        // (формат хранения Guid в SQLite — деталь провайдера).
        var raw = await ReadRawAsync("SELECT Title || '|' || Doctor FROM MedicalRecords");
        raw.Should().NotContain("Общий анализ", "в БД должен лежать шифротекст");
        raw.Should().NotContain("Айболит");
        raw.Should().Contain("enc:v1:");

        var viaEf = await NewContext().MedicalRecords.AsNoTracking().SingleAsync(r => r.Id == record.Id);
        viaEf.Title.Should().Be("Общий анализ крови");
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

    [Fact]
    public async Task PushSubscription_EndpointAndKeys_AreCiphertextInDb_PlaintextViaEf()
    {
        var owner = Db.AddUser();
        var subscription = new PushSubscription
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            EndpointHash = "hash-value-not-encrypted",
            Endpoint = "https://fcm.googleapis.com/fcm/send/some-device-token",
            P256dh = "p256dh-key-value",
            Auth = "auth-secret-value",
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
        };
        Db.PushSubscriptions.Add(subscription);
        await Db.SaveChangesAsync();

        // EndpointHash — сознательно НЕ шифруется (нужен для upsert/lookup, см. PushSubscription.cs) —
        // должен остаться читаемым в БД как есть.
        var raw = await ReadRawAsync("SELECT Endpoint || '|' || P256dh || '|' || Auth || '|' || EndpointHash FROM PushSubscriptions");
        raw.Should().NotContain("some-device-token", "endpoint должен быть зашифрован");
        raw.Should().NotContain("p256dh-key-value");
        raw.Should().NotContain("auth-secret-value");
        raw.Should().Contain("enc:v1:");
        raw.Should().Contain("hash-value-not-encrypted", "хеш — не персональные данные, хранится открыто для lookup");

        var viaEf = await NewContext().PushSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subscription.Id);
        viaEf.Endpoint.Should().Be("https://fcm.googleapis.com/fcm/send/some-device-token");
        viaEf.P256dh.Should().Be("p256dh-key-value");
        viaEf.Auth.Should().Be("auth-secret-value");
    }

    private async Task<string> ReadRawAsync(string sql)
    {
        var connection = (SqliteConnection)Db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
