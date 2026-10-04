using System.Text.RegularExpressions;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Persistence;

/// <summary>
/// EF Core не умеет материализовать <c>Database.SqlQuery&lt;T&gt;</c> в file-local тип (C# 11 <c>file class</c>):
/// у такого типа имя, сгенерированное компилятором ("&lt;Файл&gt;F…__Row"), и запрос падает с
/// IndexOutOfRangeException в NavigationExpandingExpressionVisitor ещё на этапе трансляции — на любом
/// провайдере. Живой баг: GET /api/admin/kb/specimens (GlobalSpecimenKbService.ReadAliasesAsync). Строки raw-SQL
/// в проекте — обычные internal-классы, поэтому file-local типов в рабочих сборках не должно быть вовсе.
/// </summary>
public class SqlQueryRowTypesTests
{
    private static readonly Regex FileLocalName = new(@"^<[^>]+>F[0-9A-F]{64}__", RegexOptions.Compiled);

    [Fact]
    public void ProductionAssemblies_HaveNoFileLocalTypes()
    {
        var assemblies = new[]
        {
            typeof(AppDbContext).Assembly,
            typeof(GlobalSpecimenKbService).Assembly,
            typeof(FamilyHub.Api.Features.Admin.AdminCatalogEndpoints).Assembly,
        };

        var fileLocal = assemblies
            .SelectMany(a => a.GetTypes())
            // Наш код живёт в пространствах FamilyHub.*; file-local типы генераторов исходников (GeneratedRegex —
            // System.Text.RegularExpressions.Generated) — не строки SqlQuery, их не трогаем.
            .Where(t => (t.Namespace ?? string.Empty).StartsWith("FamilyHub", StringComparison.Ordinal))
            // Имя file-local типа: "<Файл>F" + 64 hex-символа хэша + "__" + имя. Вложенные типы компилятора
            // (замыкания, state-машины async) — другой формат и вложены, их не считаем.
            .Where(t => !t.IsNested && FileLocalName.IsMatch(t.Name))
            .Select(t => t.FullName)
            .ToList();

        fileLocal.Should().BeEmpty("file-local тип в Database.SqlQuery<T> роняет EF Core — используйте internal class");
    }
}
