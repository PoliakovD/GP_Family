using System.Data.Common;
using System.Reflection;
using System.Text;
using FamilyHub.Domain;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Security.Rotation;
using FamilyHub.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Security.Rotation;

/// <summary>
/// EncryptionRotationJob не наследует SqliteTestBase: тому базовому классу нужен ОДИН стабильный
/// (нератируемый) IFieldCipher на весь тестовый процесс (см. его комментарий про кэш модели EF).
/// Здесь наоборот — сценарий требует ДВУХ разных cipher-инстансов (до/после ротации) поверх одной
/// и той же схемы, поэтому connection/options строятся вручную, тем же приёмом
/// (shared-cache SQLite in-memory), что и в SqliteTestBase.
/// </summary>
public class EncryptionRotationJobTests : IDisposable
{
    private const string KeyV1 = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
    private const string KeyV2 = "ZmVkY2JhOTg3NjU0MzIxMGZlZGNiYTk4NzY1NDMyMTA=";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Dictionary<string, byte[]> _blobs = [];
    private readonly IFileStorage _storage;

    public EncryptionRotationJobTests()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"rotation-testdb-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        // EnableServiceProviderCaching(false): AppDbContext.OnModelCreating captures IFieldCipher
        // in a closure baked into the COMPILED MODEL, which EF caches per internal service
        // provider — normally correct (AppDbContext.cs comment: "cipher обязан быть синглтоном
        // со стабильным ключом на всё время работы процесса"). This test intentionally builds TWO
        // AppDbContext instances against the SAME options with DIFFERENT ciphers (before/after
        // rotation) — without this flag, EF would silently reuse the first instance's compiled
        // model/cipher for both, making rotation invisible to assertions.
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .EnableServiceProviderCaching(false)
            .Options;

        _storage = Substitute.For<IFileStorage>();
        _storage.SaveAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                using var copy = new MemoryStream();
                callInfo.Arg<Stream>().CopyTo(copy);
                _blobs[callInfo.ArgAt<string>(0)] = copy.ToArray();
                return Task.FromResult(callInfo.ArgAt<string>(0));
            });
        _storage.OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult<Stream>(new MemoryStream(_blobs[callInfo.ArgAt<string>(0)])));
    }

    private static IEncryptionKeyRing RingV1() =>
        new EncryptionKeyRing(new EncryptionOptions { MasterKey = KeyV1, ActiveKeyId = "v1" });

    private static IEncryptionKeyRing RotatedRing() =>
        new EncryptionKeyRing(new EncryptionOptions
        {
            MasterKey = KeyV2,
            ActiveKeyId = "v2",
            PreviousKeys = [new EncryptionKeyEntry { Id = "v1", Material = KeyV1 }],
        });

    /// <summary>Сырое значение колонки в обход IFieldCipher/ValueConverter — проверить фактический
    /// keyId-префикс на диске, а не то, что EF смог его расшифровать (расшифровать он смог бы и
    /// старым ключом через связку — это не доказывает, что перешифровка реально произошла).</summary>
    private async Task<string?> ReadRawColumnAsync(string table, string column, Guid id)
    {
        await using DbCommand command = _connection.CreateCommand();
        command.CommandText = $"SELECT \"{column}\" FROM \"{table}\" WHERE \"Id\" = @id";
        var p = command.CreateParameter();
        p.ParameterName = "@id";
        // EF Core хранит Guid в SQLite как TEXT в ВЕРХНЕМ регистре — сравнение "=" регистрозависимо.
        p.Value = id.ToString().ToUpperInvariant();
        command.Parameters.Add(p);
        return (string?)await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task RunAsync_NoRunningRun_IsNoOp()
    {
        await using var db = new AppDbContext(_options, new AesGcmFieldCipher(RingV1()));
        await db.Database.EnsureCreatedAsync();

        var job = new EncryptionRotationJob(
            db, RingV1(), _storage, new AesGcmFileCipher(RingV1()), NullLogger<EncryptionRotationJob>.Instance);

        await job.RunAsync();

        (await db.EncryptionRotationRuns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_RotatesFieldsAndBlobs_EndToEnd()
    {
        var familyId = Guid.NewGuid();
        var medicalRecordId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        const string storageKey = "blobs/aa/bb/rotation-test";

        // --- Сидирование под активным v1 ---
        await using (var seedDb = new AppDbContext(_options, new AesGcmFieldCipher(RingV1())))
        {
            await seedDb.Database.EnsureCreatedAsync();

            seedDb.Families.Add(new Family { Id = familyId, Name = "Тест", CreatedAt = DateTime.UtcNow });
            seedDb.MedicalRecords.Add(new MedicalRecord
            {
                Id = medicalRecordId,
                OwnerUserId = Guid.NewGuid(),
                Kind = MedicalRecordKind.Analysis,
                Title = "Иванов Иван",
                Doctor = "Петров",
                Description = "плановый анализ",
                RecordDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CreatedAt = DateTime.UtcNow,
            });
            seedDb.Birthdays.Add(new Birthday
            {
                Id = Guid.NewGuid(), FamilyId = familyId, PersonName = "Дочь", Date = new DateOnly(2015, 3, 1),
            });
            seedDb.FamilyDependents.Add(new FamilyDependent
            {
                Id = Guid.NewGuid(), FamilyId = familyId, FirstName = "Кот Барсик", IsPet = true, Gender = Gender.Male,
                CreatedByUserId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
            });
            seedDb.PushSubscriptions.Add(new PushSubscription
            {
                Id = Guid.NewGuid(), UserId = Guid.NewGuid(), EndpointHash = "hash-1",
                Endpoint = "https://push.example/abc", P256dh = "pubkey", Auth = "authsecret",
                CreatedAt = DateTime.UtcNow, LastUsedAt = DateTime.UtcNow,
            });
            seedDb.FileAttachments.Add(new FileAttachment
            {
                Id = attachmentId, OwnerType = FileOwnerType.MedicalRecord, OwnerId = medicalRecordId,
                StorageKey = storageKey, FileName = "скан.pdf", ContentType = "application/pdf",
                SizeBytes = 10, IsEncrypted = true, KeyId = "v1", UploadedAt = DateTime.UtcNow,
            });
            await seedDb.SaveChangesAsync();

            using var encryptedBlob = new MemoryStream();
            await new AesGcmFileCipher(RingV1()).EncryptAsync(
                new MemoryStream(Encoding.UTF8.GetBytes("scan-bytes")), encryptedBlob);
            _blobs[storageKey] = encryptedBlob.ToArray();
        }

        // --- Ротация: активен v2, v1 — отставной ---
        var rotatedRing = RotatedRing();
        await using var db = new AppDbContext(_options, new AesGcmFieldCipher(rotatedRing));
        var run = new EncryptionRotationRun
        {
            Id = Guid.NewGuid(), TargetKeyId = "v2", Status = EncryptionRotationStatus.Running, StartedAt = DateTime.UtcNow,
        };
        db.EncryptionRotationRuns.Add(run);
        await db.SaveChangesAsync();

        var job = new EncryptionRotationJob(
            db, rotatedRing, _storage, new AesGcmFileCipher(rotatedRing), NullLogger<EncryptionRotationJob>.Instance);
        await job.RunAsync();

        // --- Проверки ---
        var finished = await db.EncryptionRotationRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        finished.Status.Should().Be(EncryptionRotationStatus.Completed);
        finished.FieldsProcessed.Should().Be(5); // MedicalRecord + Birthday + FamilyDependent + PushSubscription + FileAttachment(FileName)
        finished.FieldsTotal.Should().Be(5);
        finished.BlobsProcessed.Should().Be(1);
        finished.BlobsTotal.Should().Be(1);

        // Открытые значения читаются как прежде (round-trip через связку не сломан)...
        var reloadedRecord = await db.MedicalRecords.AsNoTracking().SingleAsync(r => r.Id == medicalRecordId);
        reloadedRecord.Title.Should().Be("Иванов Иван");
        reloadedRecord.Doctor.Should().Be("Петров");

        // ...но физически перезаписаны активным ключом, не просто остались читаемы старым.
        var rawTitle = await ReadRawColumnAsync("MedicalRecords", "Title", medicalRecordId);
        rawTitle.Should().StartWith("enc:v2:");

        var reloadedAttachment = await db.FileAttachments.AsNoTracking().SingleAsync(a => a.Id == attachmentId);
        reloadedAttachment.KeyId.Should().Be("v2");
        reloadedAttachment.FileName.Should().Be("скан.pdf");

        var rawFileName = await ReadRawColumnAsync("FileAttachments", "FileName", attachmentId);
        rawFileName.Should().StartWith("enc:v2:");

        await using var rotatedBlob = await _storage.OpenReadAsync(storageKey);
        await using var plainBlob = await new AesGcmFileCipher(rotatedRing).DecryptAsync(rotatedBlob);
        using var result = new MemoryStream();
        await plainBlob.CopyToAsync(result);
        Encoding.UTF8.GetString(result.ToArray()).Should().Be("scan-bytes");
    }

    [Fact]
    public async Task RunAsync_CancelRequestedBeforeStart_StopsImmediatelyWithoutProcessing()
    {
        await using (var seedDb = new AppDbContext(_options, new AesGcmFieldCipher(RingV1())))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.MedicalRecords.Add(new MedicalRecord
            {
                Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Kind = MedicalRecordKind.Analysis,
                Title = "Не должно перешифроваться", RecordDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CreatedAt = DateTime.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        var rotatedRing = RotatedRing();
        await using var db = new AppDbContext(_options, new AesGcmFieldCipher(rotatedRing));
        var run = new EncryptionRotationRun
        {
            Id = Guid.NewGuid(), TargetKeyId = "v2", Status = EncryptionRotationStatus.Running,
            StartedAt = DateTime.UtcNow, CancelRequested = true,
        };
        db.EncryptionRotationRuns.Add(run);
        await db.SaveChangesAsync();

        var job = new EncryptionRotationJob(
            db, rotatedRing, _storage, new AesGcmFileCipher(rotatedRing), NullLogger<EncryptionRotationJob>.Instance);
        await job.RunAsync();

        var finished = await db.EncryptionRotationRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        finished.Status.Should().Be(EncryptionRotationStatus.Cancelled);
        finished.FieldsProcessed.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ResumesFromSavedCursor_SkipsAlreadyProcessedRows()
    {
        Guid firstId, secondId;
        await using (var seedDb = new AppDbContext(_options, new AesGcmFieldCipher(RingV1())))
        {
            await seedDb.Database.EnsureCreatedAsync();
            var a = new MedicalRecord
            {
                Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Kind = MedicalRecordKind.Analysis,
                Title = "Запись A", RecordDate = DateOnly.FromDateTime(DateTime.UtcNow), CreatedAt = DateTime.UtcNow,
            };
            var b = new MedicalRecord
            {
                Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Kind = MedicalRecordKind.Analysis,
                Title = "Запись B", RecordDate = DateOnly.FromDateTime(DateTime.UtcNow), CreatedAt = DateTime.UtcNow,
            };
            seedDb.MedicalRecords.AddRange(a, b);
            await seedDb.SaveChangesAsync();

            var ordered = await seedDb.MedicalRecords.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
            firstId = ordered[0].Id;
            secondId = ordered[1].Id;
        }

        var rotatedRing = RotatedRing();
        await using var db = new AppDbContext(_options, new AesGcmFieldCipher(rotatedRing));
        var run = new EncryptionRotationRun
        {
            Id = Guid.NewGuid(), TargetKeyId = "v2", Status = EncryptionRotationStatus.Running,
            StartedAt = DateTime.UtcNow,
            // Симулируем прерванный прогон: первая запись уже "обработана" в предыдущем вызове.
            FieldsCursorId = firstId, FieldsProcessed = 1, FieldsTotal = 2,
        };
        db.EncryptionRotationRuns.Add(run);
        await db.SaveChangesAsync();

        var job = new EncryptionRotationJob(
            db, rotatedRing, _storage, new AesGcmFileCipher(rotatedRing), NullLogger<EncryptionRotationJob>.Instance);
        await job.RunAsync();

        var finished = await db.EncryptionRotationRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        finished.Status.Should().Be(EncryptionRotationStatus.Completed);
        finished.FieldsProcessed.Should().Be(2); // 1 «унаследованный» + 1 реально обработанный сейчас

        // Запись ДО курсора не тронута — осталась на v1.
        (await ReadRawColumnAsync("MedicalRecords", "Title", firstId)).Should().StartWith("enc:v1:");
        // Запись ПОСЛЕ курсора — перешифрована.
        (await ReadRawColumnAsync("MedicalRecords", "Title", secondId)).Should().StartWith("enc:v2:");
    }

    /// <summary>
    /// Список FieldEntityTypes НЕ авто-обнаруживается (см. его doc-комментарий) — эта проверка
    /// ловит забытую запись: любая сущность модели с хотя бы одним [Encrypted]-свойством обязана
    /// быть перечислена, иначе её значения молча не попадут в перешифровку при ротации.
    /// </summary>
    [Fact]
    public async Task FieldEntityTypes_CoversEveryEncryptedEntityInModel()
    {
        await using var db = new AppDbContext(_options, new AesGcmFieldCipher(RingV1()));

        var entitiesWithEncryptedProperties = db.Model.GetEntityTypes()
            .Where(et => et.GetProperties().Any(p => p.PropertyInfo?.GetCustomAttribute<EncryptedAttribute>() is not null))
            .Select(et => et.ClrType)
            .ToList();

        entitiesWithEncryptedProperties.Should().BeEquivalentTo(EncryptionRotationJob.FieldEntityTypes);
    }

    public void Dispose() => _connection.Dispose();
}
