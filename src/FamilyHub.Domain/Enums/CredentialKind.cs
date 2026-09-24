namespace FamilyHub.Domain.Enums;

/// <summary>К какому хранилищу относится ротируемая учётка приложения (ADR-0011).</summary>
public enum CredentialKind
{
    /// <summary>Роль входа приложения в Postgres (familyhub_app_a / familyhub_app_b).</summary>
    Postgres = 0,

    /// <summary>Access key service account приложения в MinIO.</summary>
    Minio = 1,
}
