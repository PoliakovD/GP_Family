using System.Text.Json;
using FamilyHub.Api.Features.Admin;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Security.Credentials;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FamilyHub.UnitTests.Features.Admin;

/// <summary>
/// Ротация учёток приложения из админ-панели (ADR-0011): порядок «сгенерировать → задеплоить →
/// отозвать», защитные правила и то, что секреты нигде не сохраняются. Хранилища — стейтфул-фейки:
/// «деплой» моделируется сменой учётки, под которой фейк считает приложение подключённым.
/// </summary>
public class AdminCredentialsServiceTests : SqliteTestBase
{
    private readonly FakeDbAdmin _pg = new();
    private readonly FakeMinioAdmin _minio = new();

    private AdminCredentialsService Service() =>
        new(Db, _pg, _minio, NullLogger<AdminCredentialsService>.Instance);

    private static async Task<CredentialRotationException> FailureOf(Func<Task> action) =>
        await Assert.ThrowsAsync<CredentialRotationException>(action);

    // ─── Postgres ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Postgres_UnderSuperuser_ReportsModeAndRefusesEverything()
    {
        _pg.SessionUser = "postgres";
        _pg.FunctionsPresent = false;

        var status = await Service().GetStatusAsync();

        status.Postgres.Mode.Should().Be("Superuser");
        (await FailureOf(() => Service().GeneratePostgresAsync("admin"))).Failure.Should().Be(CredentialFailure.NotLeastPrivilege);
        (await FailureOf(() => Service().RevokePostgresOldAsync("admin"))).Failure.Should().Be(CredentialFailure.NotLeastPrivilege);
        (await Db.CredentialRotations.CountAsync()).Should().Be(0);
        _pg.SetPasswordCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Postgres_UnderAppRoleWithoutFunctions_IsNotRotatable()
    {
        // Роль приложения уже есть, но схема familyhub_admin не установлена (bootstrap не выполнен до конца).
        _pg.SessionUser = DbAppRoles.SlotA;
        _pg.FunctionsPresent = false;

        (await Service().GetStatusAsync()).Postgres.Mode.Should().Be("Superuser");
        (await FailureOf(() => Service().GeneratePostgresAsync("admin"))).Failure.Should().Be(CredentialFailure.NotLeastPrivilege);
    }

    [Fact]
    public async Task Postgres_FullCycle_GenerateDeployRevoke_ThenRotateBack()
    {
        var service = Service();

        // 1. Генерация: пароль — для СОСЕДНЕЙ роли, наружу — строки для PROD_ENV.
        var generated = await service.GeneratePostgresAsync("admin");

        generated.EnvLines.Should().HaveCount(2);
        generated.EnvLines[0].Should().Be("DB_APP_USER=familyhub_app_b");
        var password = generated.EnvLines[1]["DB_APP_PASSWORD=".Length..];
        password.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        _pg.SetPasswordCalls.Should().ContainSingle().Which.Role.Should().Be(DbAppRoles.SlotB);
        _pg.SetPasswordCalls[0].Verifier.Should().StartWith("SCRAM-SHA-256$").And.NotContain(password, "на сервер уходит хэш, не пароль");

        var pending = (await service.GetStatusAsync()).Postgres;
        pending.Pending.Should().NotBeNull();
        pending.Pending!.Status.Should().Be("AwaitingDeploy");
        pending.RevocableOld.Should().BeNull("приложение ещё работает под старой ролью");

        // 2. Отзывать рано: новая роль ещё не применена.
        (await FailureOf(() => service.RevokePostgresOldAsync("admin"))).Failure.Should().Be(CredentialFailure.PendingRotation);
        _pg.DisableCalls.Should().BeEmpty();

        // 3. «Деплой»: приложение перезапущено под новой ролью — панель это ЗАМЕЧАЕТ сама.
        _pg.SessionUser = DbAppRoles.SlotB;
        var activated = (await service.GetStatusAsync()).Postgres;
        activated.Pending.Should().BeNull();
        activated.RevocableOld.Should().Be(DbAppRoles.SlotA);
        activated.CurrentSince.Should().NotBeNull();

        // Пока старая не отозвана, новую ротацию начинать нельзя.
        (await FailureOf(() => service.GeneratePostgresAsync("admin"))).Failure.Should().Be(CredentialFailure.OldNotRevoked);

        // 4. Отзыв: старая роль отключена, число оборванных сессий сохранено.
        _pg.TerminatedByDisable = 7;
        var revoked = await service.RevokePostgresOldAsync("admin");

        revoked.TerminatedSessions.Should().Be(7);
        _pg.DisableCalls.Should().ContainSingle().Which.Should().Be(DbAppRoles.SlotA);
        var done = (await service.GetStatusAsync()).Postgres;
        done.RevocableOld.Should().BeNull();
        (await FailureOf(() => service.RevokePostgresOldAsync("admin"))).Failure.Should().Be(CredentialFailure.NothingToRevoke);

        // 5. Следующая ротация идёт обратно: B → A.
        var back = await service.GeneratePostgresAsync("admin");
        back.EnvLines[0].Should().Be("DB_APP_USER=familyhub_app_a");
        var history = (await service.GetStatusAsync()).History;
        history.Select(h => h.Status).Should().Equal("AwaitingDeploy", "Revoked");
    }

    [Fact]
    public async Task Postgres_RegenerateWhilePending_SupersedesPrevious_AndKeepsOnePending()
    {
        var service = Service();

        await service.GeneratePostgresAsync("admin");
        var second = await service.GeneratePostgresAsync("admin");

        _pg.SetPasswordCalls.Should().HaveCount(2);
        _pg.SetPasswordCalls.Select(c => c.Role).Should().OnlyContain(r => r == DbAppRoles.SlotB, "запасной слот один — второй пароль заменяет первый");
        var rows = await Db.CredentialRotations.OrderBy(r => r.GeneratedAt).ToListAsync();
        rows.Select(r => r.Status).Should().Equal(CredentialRotationStatus.Superseded, CredentialRotationStatus.AwaitingDeploy);
        rows[1].Id.Should().Be(second.RotationId);
    }

    [Fact]
    public async Task Postgres_AppRolledBackToPreviousRole_DoesNotOfferRevokingTheRoleInUse()
    {
        var service = Service();
        await service.GeneratePostgresAsync("admin");
        _pg.SessionUser = DbAppRoles.SlotB;
        await service.GetStatusAsync();          // активирована A → B

        // Откат образа: приложение снова под A (роль B ещё действует, но не используется).
        _pg.SessionUser = DbAppRoles.SlotA;
        var status = (await service.GetStatusAsync()).Postgres;

        status.RevocableOld.Should().BeNull("отзывать сейчас можно только то, что НЕ в работе; A в работе");
        (await FailureOf(() => service.RevokePostgresOldAsync("admin"))).Failure.Should().Be(CredentialFailure.NothingToRevoke);
        _pg.DisableCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Postgres_ActivationIsRecordedOnce_NotOnEveryStatusRead()
    {
        var service = Service();
        await service.GeneratePostgresAsync("admin");
        _pg.SessionUser = DbAppRoles.SlotB;

        await service.GetStatusAsync();
        var first = (await Db.CredentialRotations.AsNoTracking().SingleAsync()).ActivatedAt;
        await Task.Delay(15);
        await service.GetStatusAsync();
        var second = (await Db.CredentialRotations.AsNoTracking().SingleAsync()).ActivatedAt;

        first.Should().NotBeNull();
        second.Should().Be(first);
    }

    [Fact]
    public async Task Postgres_RevokingTheRoleInUse_IsRefusedByTheStorage_AndReportedAsInUse()
    {
        var service = Service();
        await service.GeneratePostgresAsync("admin");
        _pg.SessionUser = DbAppRoles.SlotB;
        await service.GetStatusAsync();
        _pg.DisableError = new CredentialAdminException(CredentialAdminError.InUse, "in use");

        (await FailureOf(() => service.RevokePostgresOldAsync("admin"))).Failure.Should().Be(CredentialFailure.InUse);

        // и запись осталась Activated: отзыв не состоялся
        (await Db.CredentialRotations.SingleAsync()).Status.Should().Be(CredentialRotationStatus.Activated);
    }

    [Theory]
    [InlineData(CredentialAdminError.Unavailable, CredentialFailure.StorageUnavailable)]
    [InlineData(CredentialAdminError.Rejected, CredentialFailure.StorageRejected)]
    [InlineData(CredentialAdminError.Forbidden, CredentialFailure.StorageRejected)]
    public async Task Postgres_StorageErrors_AreMapped_AndLeaveNoRecord(CredentialAdminError error, CredentialFailure expected)
    {
        _pg.SetPasswordError = new CredentialAdminException(error, "boom");

        (await FailureOf(() => Service().GeneratePostgresAsync("admin"))).Failure.Should().Be(expected);
        (await Db.CredentialRotations.CountAsync()).Should().Be(0, "пароль не выпущен — записывать нечего");
    }

    [Fact]
    public async Task Postgres_HistoryRows_ContainNoSecrets()
    {
        var generated = await Service().GeneratePostgresAsync("admin");
        var password = generated.EnvLines[1]["DB_APP_PASSWORD=".Length..];

        var stored = await Db.CredentialRotations.AsNoTracking().ToListAsync();
        var status = await Service().GetStatusAsync();

        JsonSerializer.Serialize(stored).Should().NotContain(password);
        JsonSerializer.Serialize(status).Should().NotContain(password);
    }

    // ─── MinIO ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Minio_NotUnderServiceAccount_RefusesToRotate()
    {
        _minio.Mode = MinioCredentialMode.NotServiceAccount;

        (await Service().GetStatusAsync()).Minio.Mode.Should().Be("NotServiceAccount");
        (await FailureOf(() => Service().GenerateMinioAsync("admin"))).Failure.Should().Be(CredentialFailure.NotServiceAccount);
        _minio.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Minio_Unreachable_ReportsUnknown_AndRefusesAsUnavailable()
    {
        _minio.Mode = MinioCredentialMode.Unknown;

        (await Service().GetStatusAsync()).Minio.Mode.Should().Be("Unknown");
        (await FailureOf(() => Service().GenerateMinioAsync("admin"))).Failure.Should().Be(CredentialFailure.StorageUnavailable);
    }

    [Fact]
    public async Task Minio_FullCycle_GenerateDeployRevoke()
    {
        var service = Service();
        var oldKey = _minio.CurrentAccessKey;

        var generated = await service.GenerateMinioAsync("admin");

        generated.EnvLines.Should().HaveCount(2);
        var accessKey = generated.EnvLines[0]["MINIO_APP_ACCESS_KEY=".Length..];
        var secretKey = generated.EnvLines[1]["MINIO_APP_SECRET_KEY=".Length..];
        accessKey.Should().MatchRegex("^FHAPP[A-Z0-9]{15}$");
        secretKey.Should().MatchRegex("^[A-Za-z0-9]{40}$");
        _minio.Created.Should().ContainSingle().Which.Should().Be((accessKey, secretKey));

        // Отзывать рано — приложение всё ещё под старым ключом.
        (await FailureOf(() => service.RevokeMinioOldAsync("admin"))).Failure.Should().Be(CredentialFailure.PendingRotation);

        _minio.CurrentAccessKey = accessKey;      // «деплой»
        var status = (await service.GetStatusAsync()).Minio;
        status.Pending.Should().BeNull();
        status.RevocableOldMasked.Should().Be(AdminCredentialsService.Mask(oldKey));
        (await FailureOf(() => service.GenerateMinioAsync("admin"))).Failure.Should().Be(CredentialFailure.OldNotRevoked);

        await service.RevokeMinioOldAsync("admin");

        _minio.Deleted.Should().ContainSingle().Which.Should().Be(oldKey);
        (await Db.CredentialRotations.SingleAsync()).Status.Should().Be(CredentialRotationStatus.Revoked);
    }

    [Fact]
    public async Task Minio_RegenerateWhilePending_DeletesTheUnusedKey()
    {
        var service = Service();
        var first = await service.GenerateMinioAsync("admin");
        var firstKey = first.EnvLines[0]["MINIO_APP_ACCESS_KEY=".Length..];

        await service.GenerateMinioAsync("admin");

        _minio.Deleted.Should().ContainSingle().Which.Should().Be(firstKey, "неиспользованный ключ не должен остаться действующим");
        var rows = await Db.CredentialRotations.OrderBy(r => r.GeneratedAt).ToListAsync();
        rows.Select(r => r.Status).Should().Equal(CredentialRotationStatus.Superseded, CredentialRotationStatus.AwaitingDeploy);
    }

    [Fact]
    public async Task Minio_HistoryAndStatus_ContainNoSecretsAndMaskKeys()
    {
        var generated = await Service().GenerateMinioAsync("admin");
        var accessKey = generated.EnvLines[0]["MINIO_APP_ACCESS_KEY=".Length..];
        var secretKey = generated.EnvLines[1]["MINIO_APP_SECRET_KEY=".Length..];

        var status = await Service().GetStatusAsync();
        var json = JsonSerializer.Serialize(status);

        json.Should().NotContain(secretKey);
        json.Should().NotContain(accessKey, "в интерфейс уходит только маска ключа");
        status.Minio.Pending!.To.Should().Be(AdminCredentialsService.Mask(accessKey));
        (await Db.CredentialRotations.AsNoTracking().ToListAsync()).Should().OnlyContain(r => r.ToIdentity == accessKey, "в БД — идентификатор ключа (не секрет), нужный для отзыва");
        JsonSerializer.Serialize(await Db.CredentialRotations.AsNoTracking().ToListAsync()).Should().NotContain(secretKey);
    }

    [Theory]
    [InlineData("FHAPPABCDEFGHIJKLMNO", "FHAP…LMNO")]
    [InlineData("minioadmin", "mi…")]
    [InlineData("", "…")]
    public void Mask_ShowsOnlyEdges(string identity, string expected) =>
        AdminCredentialsService.Mask(identity).Should().Be(expected);

    // ─── Фейки ──────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeDbAdmin : IDbCredentialAdmin
    {
        public string SessionUser { get; set; } = DbAppRoles.SlotA;
        public bool FunctionsPresent { get; set; } = true;
        public int TerminatedByDisable { get; set; }
        public CredentialAdminException? SetPasswordError { get; set; }
        public CredentialAdminException? DisableError { get; set; }
        public List<(string Role, string Verifier)> SetPasswordCalls { get; } = [];
        public List<string> DisableCalls { get; } = [];

        public Task<DbSessionInfo> GetSessionAsync(CancellationToken ct = default) =>
            Task.FromResult(new DbSessionInfo(SessionUser, FunctionsPresent));

        public Task<IReadOnlyList<DbRoleSlot>> GetSlotsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DbRoleSlot>>(
            [
                new DbRoleSlot(DbAppRoles.SlotA, true, true, 3),
                new DbRoleSlot(DbAppRoles.SlotB, false, false, 0),
            ]);

        public Task SetPasswordAsync(string role, string scramVerifier, CancellationToken ct = default)
        {
            if (SetPasswordError is not null) throw SetPasswordError;
            SetPasswordCalls.Add((role, scramVerifier));
            return Task.CompletedTask;
        }

        public Task<int> DisableAsync(string role, CancellationToken ct = default)
        {
            if (DisableError is not null) throw DisableError;
            DisableCalls.Add(role);
            return Task.FromResult(TerminatedByDisable);
        }
    }

    private sealed class FakeMinioAdmin : IMinioCredentialAdmin
    {
        public string CurrentAccessKey { get; set; } = "FHAPPOLDKEY00000001";
        public MinioCredentialMode Mode { get; set; } = MinioCredentialMode.ServiceAccount;
        public List<(string AccessKey, string SecretKey)> Created { get; } = [];
        public List<string> Deleted { get; } = [];

        public Task<MinioCredentialMode> GetModeAsync(CancellationToken ct = default) => Task.FromResult(Mode);

        public Task CreateServiceAccountAsync(string accessKey, string secretKey, string name, CancellationToken ct = default)
        {
            Created.Add((accessKey, secretKey));
            return Task.CompletedTask;
        }

        public Task DeleteServiceAccountAsync(string accessKey, CancellationToken ct = default)
        {
            Deleted.Add(accessKey);
            return Task.CompletedTask;
        }
    }
}
