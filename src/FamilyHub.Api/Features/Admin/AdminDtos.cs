namespace FamilyHub.Api.Features.Admin;

public record UsersOverviewDto(
    int Total, int TelegramOnly, int PwaOnly, int Both,
    int NewLast7Days, int NewLast30Days, int LockedOut);

public record FamiliesOverviewDto(int Total, int WithActiveMembers, double AverageActiveMembers);

public record DomainCountsDto(
    int MedicalRecords, int Medications, int ExpiredMedications,
    int Birthdays, int Attachments, int FamilyDependents);

public record AdminOverviewDto(UsersOverviewDto Users, FamiliesOverviewDto Families, DomainCountsDto Domain);

public record StorageReconciliationDto(int OrphanedBlobs, int BrokenAttachments);

public record AdminStorageStatsDto(
    long BucketSizeBytes, int BucketObjectCount,
    long AttachmentsSizeBytesInDb, int AttachmentsCountInDb,
    StorageReconciliationDto Reconciliation, DateTime ComputedAt);

public record OutboxBacklogDto(int UndeliveredBatches, DateTime? OldestUndeliveredAt);

public record HangfireQueueDto(string Name, long EnqueuedCount);

public record AdminSystemStatsDto(
    OutboxBacklogDto Outbox, IReadOnlyList<HangfireQueueDto> HangfireQueues, long HangfireFailedJobsTotal,
    bool PostgresHealthy, bool MinioHealthy, bool KafkaHealthy);

public record KeyIdCountDto(string KeyId, int Count);

public record EncryptionKeyDistributionDto(
    IReadOnlyList<KeyIdCountDto> FieldValues, IReadOnlyList<KeyIdCountDto> AttachmentBlobs);

public record AdminSecurityStatsDto(
    EncryptionKeyDistributionDto EncryptionDistribution,
    int CrossUserMedicalAccessLast30Days,
    int UsersWithoutCurrentConsent,
    int ActiveSessions,
    int DataProtectionKeyCount,
    DateTime? OldestDataProtectionKeyCreatedAt);

public record EncryptionKeyRingDto(string ActiveKeyId, IReadOnlyList<string> PreviousKeyIds);
public record JwtKeyRingDto(string ActiveKeyId, IReadOnlyList<string> PreviousKeyIds);
public record DownloadKeyRingDto(int PreviousKeyCount);

public record AdminKeyRingsDto(
    EncryptionKeyRingDto Encryption, JwtKeyRingDto Jwt, DownloadKeyRingDto Attachments);

public record RotationStatusDto(
    Guid? RunId, string? TargetKeyId, string? Status,
    DateTime? StartedAt, DateTime? FinishedAt, string? LastError,
    int FieldsProcessed, int FieldsTotal, int BlobsProcessed, int BlobsTotal);
