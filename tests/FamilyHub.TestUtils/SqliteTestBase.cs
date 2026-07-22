using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FamilyHub.TestUtils;

/// <summary>
/// Базовый класс для unit-тестов сервисов, работающих с БД. SQLite in-memory (НЕ EF InMemory
/// provider) — критично, потому что InviteService.RedeemInviteAsync использует реальную
/// транзакцию (BeginTransactionAsync), а UserProvisioningService/ReminderScanJob/
/// MedicalRecordHidden полагаются на DbUpdateException от UNIQUE-индекса при гонке. EF InMemory
/// не поддерживает ни то, ни другое — SQLite поддерживает оба и остаётся быстрым, без Docker.
///
/// xUnit создаёт новый экземпляр класса теста на каждый тест-метод => конструктор отрабатывает
/// заново на каждый тест => каждый тест получает свою чистую БД (изоляция без явной очистки).
/// </summary>
public abstract class SqliteTestBase : IDisposable
{
    /// <summary>
    /// Единый на весь тестовый процесс cipher: EF кэширует модель контекста с конвертером,
    /// захватившим первый экземпляр, — ключ обязан быть стабильным между тестами.
    /// </summary>
    protected static readonly IFieldCipher TestFieldCipher = new AesGcmFieldCipher(
        Options.Create(new EncryptionOptions { MasterKey = DesignTimeDbContextFactory.DevMasterKey }));

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    /// <summary>Открытый на всё время теста контекст — большинству тестов достаточно одного.</summary>
    protected AppDbContext Db { get; }

    protected SqliteTestBase()
    {
        // ":memory:" с закрытием соединения теряет БД — держим соединение открытым явно,
        // а не доверяем connection pooling, на всё время жизни теста.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        Db = new AppDbContext(_options, TestFieldCipher);
        Db.Database.EnsureCreated();
    }

    /// <summary>
    /// Новый AppDbContext на том же открытом соединении (та же БД) — нужен там, где важно
    /// проверить персистентное состояние без change tracker первого контекста (например,
    /// after-the-fact проверка, что гонка не создала дубликат).
    /// </summary>
    protected AppDbContext NewContext() => new(_options, TestFieldCipher);

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
