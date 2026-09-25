namespace FamilyHub.Infrastructure.Security.Credentials;

/// <summary>Имена ролей Postgres, создаваемых deploy/scripts/sql/bootstrap-roles.sql (ADR-0011).</summary>
public static class DbAppRoles
{
    public const string Owner = "familyhub_owner";
    public const string SlotA = "familyhub_app_a";
    public const string SlotB = "familyhub_app_b";

    public static bool IsAppSlot(string role) => role is SlotA or SlotB;

    /// <summary>Соседний слот: работает A — ротируем в B и наоборот.</summary>
    public static string Sibling(string role) => role == SlotA ? SlotB : SlotA;
}
