namespace FamilyHub.Domain;

/// <summary>
/// Маркер поля с ПДн, шифруемого at-rest (этап 2, 152-ФЗ). Инфраструктура (AppDbContext)
/// навешивает на такие свойства EF ValueConverter с AES-256-GCM — в БД лежит шифротекст,
/// приложение работает с открытым значением. Ключ хранится вне БД (env).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EncryptedAttribute : Attribute;
