using FamilyHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FamilyHub.Infrastructure.Persistence;

/// <summary>
/// Позволяет `dotnet ef migrations`/`database update` строить AppDbContext без запуска
/// хоста Api. Строка подключения берётся из переменной окружения, иначе — дефолт для
/// локальной разработки. Ключ шифрования для design-time не важен (модель строится,
/// данные не читаются) — фиксированный dev-ключ.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>32 байта в base64 — общий dev/design-ключ (НЕ для прода).</summary>
    public const string DevMasterKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    /// <summary>Cipher с dev-ключом (связка из одного ключа) — для design-time и тестовых фабрик.</summary>
    public static IFieldCipher CreateDevCipher() =>
        new AesGcmFieldCipher(new EncryptionKeyRing(new EncryptionOptions { MasterKey = DevMasterKey }));

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("FAMILYHUB_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=familyhub;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString);

        return new AppDbContext(optionsBuilder.Options, CreateDevCipher());
    }
}
