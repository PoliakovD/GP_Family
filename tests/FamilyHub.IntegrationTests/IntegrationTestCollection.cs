using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Один Postgres-контейнер на всю коллекцию интеграционных тестов (а не на каждый тест-класс) —
/// поднять/смигрировать его не бесплатно. Изоляция между тестами — через свежие Guid/имена,
/// а не TRUNCATE между тестами (см. план, раздел Integration-тестов).
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationTestCollection : ICollectionFixture<FamilyHubWebFactory>
{
    public const string Name = "FamilyHub integration tests";
}
