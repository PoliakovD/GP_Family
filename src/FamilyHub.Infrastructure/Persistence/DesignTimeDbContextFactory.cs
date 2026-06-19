using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FamilyHub.Infrastructure.Persistence;

/// <summary>
/// Позволяет `dotnet ef migrations`/`database update` строить AppDbContext без запуска
/// хоста Api. Строка подключения берётся из переменной окружения, иначе — дефолт для
/// локальной разработки.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("FAMILYHUB_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=familyhub;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString);

        return new AppDbContext(optionsBuilder.Options);
    }
}
