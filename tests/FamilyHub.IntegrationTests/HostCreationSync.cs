namespace FamilyHub.IntegrationTests;

/// <summary>
/// Глобальный лок на создание хоста WebApplicationFactory&lt;Program&gt;. Коллекции xUnit идут
/// параллельно, а HostFactoryResolver ловит построенный IHost через статический
/// DiagnosticListener — при одновременном старте двух фабрик одного entry point слушатель
/// одной фабрики перехватывает хост другой, и та падает с
/// «The entry point exited without ever building an IHost». Сериализация создания хостов
/// убирает гонку; сами тесты после старта работают параллельно как раньше.
/// </summary>
public static class HostCreationSync
{
    public static readonly object Lock = new();
}
