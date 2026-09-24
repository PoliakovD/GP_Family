using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Containers;
using FamilyHub.Api.Features.Admin;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security.Credentials;
using FamilyHub.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Postgres 17 + MinIO (наш образ) + хост приложения, настроенные КАК ПОСЛЕ первичной настройки на проде
/// (ADR-0011): Postgres — роли familyhub_app_a/b и функции ротации, MinIO — пользователь с политикой на
/// один бакет и его service account, под которыми и стартует хост. Плюс включённая админ-панель.
/// </summary>
public class RotationWebFactory : LeastPrivilegeWebFactory
{
    public const string BaseMinioAccessKey = "FHAPPTEST0000000AAA1";
    public const string BaseMinioSecretKey = "BaseSecretBaseSecretBaseSecret000000000A";
    public const string Bucket = "familyhub";

    protected override string HostMinioAccessKey => BaseMinioAccessKey;

    protected override string HostMinioSecretKey => BaseMinioSecretKey;

    protected override async Task AfterMigrationsAsync(PostgreSqlContainer postgresContainer)
    {
        await base.AfterMigrationsAsync(postgresContainer);
        var result = await McAsync(SetupScript());
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Настройка MinIO завершилась с кодом {result.ExitCode}: {result.Stderr}");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Admin:Enabled", "true");
        builder.UseSetting("Admin:User", AdminWebFactory.TestUser);
        builder.UseSetting("Admin:Password", AdminWebFactory.TestPassword);
        builder.UseSetting("Admin:SessionLifetime", "00:10:00");
    }

    public string MinioEndpoint => new Uri(MinioContainer.GetConnectionString()).Authority;

    /// <summary>Выполняет shell-скрипт с mc внутри контейнера MinIO под root (как скрипты deploy/scripts).</summary>
    public Task<ExecResult> McAsync(string script) =>
        MinioContainer.ExecAsync(["sh", "-c", "export MC_CONFIG_DIR=/tmp/mc-test; " +
            $"mc alias set local http://localhost:9000 {MinioContainer.GetAccessKey()} {MinioContainer.GetSecretKey()} >/dev/null && " + script]);

    /// <summary>Пользователь familyhub-app с политикой на бакет и service account с фиксированными ключами —
    /// то же, что делает bootstrap-app-credentials.sh.</summary>
    private static string SetupScript() =>
        $"mc mb --ignore-existing local/{Bucket} >/dev/null && " +
        "printf '%s' '{\"Version\":\"2012-10-17\",\"Statement\":[" +
        $"{{\"Effect\":\"Allow\",\"Action\":[\"s3:GetBucketLocation\",\"s3:ListBucket\",\"s3:ListBucketMultipartUploads\"],\"Resource\":[\"arn:aws:s3:::{Bucket}\"]}}," +
        $"{{\"Effect\":\"Allow\",\"Action\":[\"s3:GetObject\",\"s3:PutObject\",\"s3:DeleteObject\",\"s3:AbortMultipartUpload\",\"s3:ListMultipartUploadParts\"],\"Resource\":[\"arn:aws:s3:::{Bucket}/*\"]}}" +
        "]}' > /tmp/fh-policy.json && " +
        "mc admin policy create local familyhub-app /tmp/fh-policy.json >/dev/null && " +
        "mc admin user add local familyhub-app familyhub-app-user-password-1 >/dev/null && " +
        "mc admin policy attach local familyhub-app --user familyhub-app >/dev/null && " +
        $"mc admin user svcacct add local familyhub-app --access-key {BaseMinioAccessKey} --secret-key {BaseMinioSecretKey} --name familyhub-app >/dev/null";

    /// <summary>Возвращает «базовый» service account хоста на место (после теста, который его отозвал).</summary>
    public Task<ExecResult> RestoreBaseMinioKeyAsync() =>
        McAsync($"mc admin user svcacct rm local {BaseMinioAccessKey} >/dev/null 2>&1; " +
                $"mc admin user svcacct add local familyhub-app --access-key {BaseMinioAccessKey} --secret-key {BaseMinioSecretKey} --name familyhub-app >/dev/null");
}

public class CredentialRotationTests(RotationWebFactory factory) : IClassFixture<RotationWebFactory>, IAsyncLifetime
{
    // ─── Общее ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Каждый тест стартует с чистого состояния ротаций и ролей: таблица истории пуста, роль B
    /// закрыта, роль A — с исходным паролем (тесты отзыва её отключают и возвращают).</summary>
    public async Task InitializeAsync()
    {
        await using var su = await OpenAsync(factory.SuperuserConnection);
        await ExecuteAsync(su, "DELETE FROM public.\"CredentialRotations\"");
        await ExecuteAsync(su, $"ALTER ROLE {LeastPrivilegeWebFactory.AppRoleB} WITH NOLOGIN PASSWORD NULL");
        (await factory.RunBootstrapAsync(LeastPrivilegeWebFactory.AppAPassword)).ExitCode.Should().Be(0);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword })).EnsureSuccessStatusCode();
        return client;
    }

    private IMinioClient S3(string accessKey, string secretKey) =>
        new MinioClient().WithEndpoint(factory.MinioEndpoint).WithCredentials(accessKey, secretKey).WithSSL(false).Build();

    private static Task PutAsync(IMinioClient s3, string name)
    {
        var bytes = "probe"u8.ToArray();
        return s3.PutObjectAsync(new PutObjectArgs().WithBucket(RotationWebFactory.Bucket).WithObject(name)
            .WithStreamData(new MemoryStream(bytes)).WithObjectSize(bytes.Length));
    }

    private MinioAdminClient MinioAdmin(string accessKey, string secretKey, string? endpoint = null) =>
        new(new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
            Options.Create(new MinioOptions { Endpoint = endpoint ?? factory.MinioEndpoint, AccessKey = accessKey, SecretKey = secretKey, UseSsl = false }));

    /// <summary>Сервис ротации так, как он выглядит в приложении, ПЕРЕЗАПУЩЕННОМ под другой учёткой:
    /// собственный DbContext под указанной ролью Postgres и клиент MinIO с указанным ключом. Это и есть
    /// «деплой» из сценария ротации — приложение стартует уже с новыми значениями из PROD_ENV.</summary>
    private AdminCredentialsService RestartedApp(string pgRole, string pgPassword, string minioAccessKey, string minioSecretKey, out AppDbContext db)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionAs(pgRole, pgPassword)).Options;
        db = new AppDbContext(options, DesignTimeDbContextFactory.CreateDevCipher());
        return new AdminCredentialsService(db, new NpgsqlDbCredentialAdmin(db), MinioAdmin(minioAccessKey, minioSecretKey), NullLogger<AdminCredentialsService>.Instance);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

    private record Generated(Guid RotationId, List<string> EnvLines);
    private record ErrorBody(string Code);

    private static string Value(Generated g, string name) =>
        g.EnvLines.Single(l => l.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];

    // ─── MinIO: клиент admin API против настоящего сервера ─────────────────────────────────────

    [Fact]
    public async Task MinioAdmin_DetectsServiceAccount_AndDistinguishesRootAndUnreachable()
    {
        (await MinioAdmin(RotationWebFactory.BaseMinioAccessKey, RotationWebFactory.BaseMinioSecretKey).GetModeAsync())
            .Should().Be(MinioCredentialMode.ServiceAccount);

        (await MinioAdmin("minioadmin", "minioadmin").GetModeAsync())
            .Should().Be(MinioCredentialMode.NotServiceAccount, "root — не service account, ротировать у него нечего");

        (await MinioAdmin(RotationWebFactory.BaseMinioAccessKey, RotationWebFactory.BaseMinioSecretKey, "127.0.0.1:1").GetModeAsync())
            .Should().Be(MinioCredentialMode.Unknown);
    }

    [Fact]
    public async Task MinioAdmin_CreatedKey_IsAcceptedByMinioAndWorksWithTheBucket_DeleteRevokesIt()
    {
        // Ключи — в формате нашего генератора: заодно проверяем, что MinIO принимает именно такие.
        var accessKey = SecretGenerator.MinioAccessKey();
        var secretKey = SecretGenerator.MinioSecretKey();
        var admin = MinioAdmin(RotationWebFactory.BaseMinioAccessKey, RotationWebFactory.BaseMinioSecretKey);

        await admin.CreateServiceAccountAsync(accessKey, secretKey, "rotation-test");

        await PutAsync(S3(accessKey, secretKey), "rotation-created-key-object");

        await admin.DeleteServiceAccountAsync(accessKey);
        await Assert.ThrowsAnyAsync<Exception>(() => PutAsync(S3(accessKey, secretKey), "rotation-after-delete"));

        // отзыв идемпотентен: уже удалённый ключ — не ошибка
        await admin.DeleteServiceAccountAsync(accessKey);
    }

    [Fact]
    public async Task MinioAdmin_WithWrongSecret_IsRejected()
    {
        var wrong = MinioAdmin(RotationWebFactory.BaseMinioAccessKey, "definitely-not-the-secret-key-000000000");

        var ex = await Assert.ThrowsAsync<CredentialAdminException>(() =>
            wrong.CreateServiceAccountAsync(SecretGenerator.MinioAccessKey(), SecretGenerator.MinioSecretKey(), "rotation-test"));

        ex.Error.Should().Be(CredentialAdminError.Rejected);
        ex.Message.Should().NotContain("definitely-not-the-secret", "сообщение об ошибке не должно нести секрет");
    }

    // ─── Postgres: SCRAM и функции ротации против настоящего сервера ───────────────────────────

    [Fact]
    public async Task Scram_MatchesWhatPostgresDerivesItself_AndPostgresAcceptsTheVerifier()
    {
        // 1. Наша реализация SCRAM даёт РОВНО то, что Postgres сам кладёт в pg_authid (при той же соли).
        const string password = "scram-cross-check-Password_123";
        var role = $"lp_scram_{Guid.NewGuid():N}";
        string serverVerifier;
        await using (var su = await OpenAsync(factory.SuperuserConnection))
        {
            await ExecuteAsync(su, $"SET password_encryption = 'scram-sha-256'; CREATE ROLE {role} PASSWORD '{password}'");
            await using var command = new NpgsqlCommand($"SELECT rolpassword FROM pg_authid WHERE rolname = '{role}'", su);
            serverVerifier = (string)(await command.ExecuteScalarAsync())!;
            await ExecuteAsync(su, $"DROP ROLE {role}");
        }

        var parts = serverVerifier["SCRAM-SHA-256$".Length..].Split('$', ':');
        var iterations = int.Parse(parts[0]);
        var salt = Convert.FromBase64String(parts[1]);
        ScramSha256Verifier.Create(password, salt, iterations).Should().Be(serverVerifier);

        // 2. Верификатор, выпущенный нами, принимается функцией и даёт рабочий вход.
        var generated = SecretGenerator.PostgresPassword();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword)).Options;
        await using var db = new AppDbContext(options, DesignTimeDbContextFactory.CreateDevCipher());
        var pg = new NpgsqlDbCredentialAdmin(db);

        await pg.SetPasswordAsync(LeastPrivilegeWebFactory.AppRoleB, ScramSha256Verifier.Create(generated));

        await using var asB = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleB, generated, pooled: false));
        (await new NpgsqlCommand("SELECT session_user::text", asB).ExecuteScalarAsync()).Should().Be(LeastPrivilegeWebFactory.AppRoleB);
    }

    [Fact]
    public async Task DbAdmin_MapsFunctionErrors_ToTypedExceptions()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword)).Options;
        await using var db = new AppDbContext(options, DesignTimeDbContextFactory.CreateDevCipher());
        var pg = new NpgsqlDbCredentialAdmin(db);
        var verifier = ScramSha256Verifier.Create("whatever-password");

        (await Assert.ThrowsAsync<CredentialAdminException>(() => pg.SetPasswordAsync("postgres", verifier))).Error.Should().Be(CredentialAdminError.Forbidden);
        (await Assert.ThrowsAsync<CredentialAdminException>(() => pg.SetPasswordAsync(LeastPrivilegeWebFactory.AppRoleB, "plaintext-not-a-verifier"))).Error.Should().Be(CredentialAdminError.InvalidVerifier);
        (await Assert.ThrowsAsync<CredentialAdminException>(() => pg.DisableAsync(LeastPrivilegeWebFactory.AppRoleA))).Error.Should().Be(CredentialAdminError.InUse);

        var session = await pg.GetSessionAsync();
        session.SessionUser.Should().Be(LeastPrivilegeWebFactory.AppRoleA);
        session.RotationFunctionsPresent.Should().BeTrue();
        (await pg.GetSlotsAsync()).Select(s => s.RoleName).Should().Equal(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppRoleB);
    }

    // ─── HTTP API ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/admin/credentials")]
    [InlineData("POST", "/api/admin/credentials/postgres/generate")]
    [InlineData("POST", "/api/admin/credentials/postgres/revoke-old")]
    [InlineData("POST", "/api/admin/credentials/minio/generate")]
    [InlineData("POST", "/api/admin/credentials/minio/revoke-old")]
    public async Task Endpoints_WithoutAdminSession_Return401(string method, string path)
    {
        var response = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Status_ReportsLeastPrivilegeOnBothSides_WithSlotsAndMaskedKey()
    {
        var client = await AdminClientAsync();

        var status = await ReadAsync<CredentialsStatusDto>(await client.GetAsync("/api/admin/credentials"));

        status.Postgres.Mode.Should().Be("LeastPrivilege");
        status.Postgres.SessionUser.Should().Be(LeastPrivilegeWebFactory.AppRoleA);
        status.Postgres.Slots.Select(s => s.Role).Should().Equal(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppRoleB);
        status.Minio.Mode.Should().Be("ServiceAccount");
        status.Minio.AccessKeyMasked.Should().Be(AdminCredentialsService.Mask(RotationWebFactory.BaseMinioAccessKey));
    }

    [Fact]
    public async Task Postgres_GenerateOverHttp_ReturnsCredentialsThatActuallyLogIn_AndKeepsNoSecret()
    {
        var client = await AdminClientAsync();

        var response = await client.PostAsync("/api/admin/credentials/postgres/generate", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Headers.CacheControl?.NoStore.Should().BeTrue("ответ несёт секрет — кэшировать его нельзя");
        var generated = await ReadAsync<Generated>(response);
        Value(generated, "DB_APP_USER").Should().Be(LeastPrivilegeWebFactory.AppRoleB);
        var password = Value(generated, "DB_APP_PASSWORD");

        await using (var asB = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleB, password, pooled: false)))
            (await new NpgsqlCommand("SELECT session_user::text", asB).ExecuteScalarAsync()).Should().Be(LeastPrivilegeWebFactory.AppRoleB);

        // в БД — только метаданные, а повторный чтение статуса секрет не отдаёт
        await using var su = await OpenAsync(factory.SuperuserConnection);
        var stored = (string)(await new NpgsqlCommand(
            "SELECT string_agg(concat_ws('|', \"FromIdentity\", \"ToIdentity\", \"Status\", \"GeneratedBy\"), ';') FROM public.\"CredentialRotations\"", su).ExecuteScalarAsync())!;
        stored.Should().NotContain(password);
        (await (await client.GetAsync("/api/admin/credentials")).Content.ReadAsStringAsync()).Should().NotContain(password);
    }

    [Fact]
    public async Task Postgres_RevokeBeforeDeploy_IsRefusedWith409PendingRotation()
    {
        var client = await AdminClientAsync();
        (await client.PostAsync("/api/admin/credentials/postgres/generate", null)).EnsureSuccessStatusCode();

        var response = await client.PostAsync("/api/admin/credentials/postgres/revoke-old", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync<ErrorBody>(response)).Code.Should().Be("pending_rotation");
    }

    [Fact]
    public async Task Postgres_FullRotationCycle_AgainstRealServers()
    {
        // 1. Админ-панель (хост под ролью A) выпускает пароль для B.
        var client = await AdminClientAsync();
        var generated = await ReadAsync<Generated>(await client.PostAsync("/api/admin/credentials/postgres/generate", null));
        var passwordB = Value(generated, "DB_APP_PASSWORD");

        try
        {
            // 2. «Деплой»: приложение перезапущено под B (и под тем же ключом MinIO).
            var restarted = RestartedApp(LeastPrivilegeWebFactory.AppRoleB, passwordB, RotationWebFactory.BaseMinioAccessKey, RotationWebFactory.BaseMinioSecretKey, out var dbB);
            await using var _ = dbB;

            var activated = await restarted.GetStatusAsync();
            activated.Postgres.SessionUser.Should().Be(LeastPrivilegeWebFactory.AppRoleB);
            activated.Postgres.Pending.Should().BeNull("панель сама заметила, что приложение работает под новой ролью");
            activated.Postgres.RevocableOld.Should().Be(LeastPrivilegeWebFactory.AppRoleA);

            // Живое соединение под старой ролью A (пул хоста) — перед отзывом оно есть.
            await using var oldPooled = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword));

            // 3. Отзыв старой роли из перезапущённого приложения.
            var revoked = await restarted.RevokePostgresOldAsync("admin");

            revoked.TerminatedSessions.Should().BeGreaterThanOrEqualTo(1, "открытые сессии отозванной роли обрываются");
            await Assert.ThrowsAnyAsync<Exception>(async () => await new NpgsqlCommand("SELECT 1", oldPooled).ExecuteScalarAsync());
            (await Assert.ThrowsAnyAsync<PostgresException>(async () =>
                await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword, pooled: false))))
                .SqlState.Should().BeOneOf("28000", "28P01");

            // Новая роль работает, ротация завершена.
            await using var asB = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleB, passwordB, pooled: false));
            (await new NpgsqlCommand("SELECT 1", asB).ExecuteScalarAsync()).Should().Be(1);
            (await restarted.GetStatusAsync()).History.Should().ContainSingle().Which.Status.Should().Be("Revoked");
        }
        finally
        {
            // Роль A нужна остальным тестам класса (хост работает под ней): возвращаем.
            (await factory.RunBootstrapAsync(LeastPrivilegeWebFactory.AppAPassword)).ExitCode.Should().Be(0);
        }
    }

    [Fact]
    public async Task Minio_GenerateOverHttp_ReturnsKeyThatWorksWithTheBucket()
    {
        var client = await AdminClientAsync();

        var response = await client.PostAsync("/api/admin/credentials/minio/generate", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var generated = await ReadAsync<Generated>(response);
        var accessKey = Value(generated, "MINIO_APP_ACCESS_KEY");
        var secretKey = Value(generated, "MINIO_APP_SECRET_KEY");
        try
        {
            // ключ, выпущенный админ-панелью (зашифрованный запрос + подпись — принят настоящим MinIO), рабочий
            await PutAsync(S3(accessKey, secretKey), "rotation-generated-key-object");

            var status = await ReadAsync<CredentialsStatusDto>(await client.GetAsync("/api/admin/credentials"));
            status.Minio.Pending.Should().NotBeNull();
            status.Minio.Pending!.To.Should().Be(AdminCredentialsService.Mask(accessKey));
            JsonSerializer.Serialize(status).Should().NotContain(secretKey).And.NotContain(accessKey);
        }
        finally
        {
            await MinioAdmin(RotationWebFactory.BaseMinioAccessKey, RotationWebFactory.BaseMinioSecretKey).DeleteServiceAccountAsync(accessKey);
        }
    }

    [Fact]
    public async Task Minio_FullRotationCycle_AgainstRealServers()
    {
        var client = await AdminClientAsync();
        var generated = await ReadAsync<Generated>(await client.PostAsync("/api/admin/credentials/minio/generate", null));
        var newKey = Value(generated, "MINIO_APP_ACCESS_KEY");
        var newSecret = Value(generated, "MINIO_APP_SECRET_KEY");

        try
        {
            // «Деплой»: приложение перезапущено с новым ключом MinIO (Postgres — прежняя роль A).
            var restarted = RestartedApp(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword, newKey, newSecret, out var db);
            await using var _ = db;

            var activated = await restarted.GetStatusAsync();
            activated.Minio.Pending.Should().BeNull();
            activated.Minio.RevocableOldMasked.Should().Be(AdminCredentialsService.Mask(RotationWebFactory.BaseMinioAccessKey));

            await restarted.RevokeMinioOldAsync("admin");

            // старый ключ больше не работает, новый — работает
            await Assert.ThrowsAnyAsync<Exception>(() => PutAsync(S3(RotationWebFactory.BaseMinioAccessKey, RotationWebFactory.BaseMinioSecretKey), "must-fail"));
            await PutAsync(S3(newKey, newSecret), "rotation-after-revoke");
            (await restarted.GetStatusAsync()).History.Should().ContainSingle().Which.Status.Should().Be("Revoked");
        }
        finally
        {
            // хост работает под базовым ключом — возвращаем его, а выпущенный удаляем
            (await factory.RestoreBaseMinioKeyAsync()).ExitCode.Should().Be(0);
            await MinioAdmin(RotationWebFactory.BaseMinioAccessKey, RotationWebFactory.BaseMinioSecretKey).DeleteServiceAccountAsync(newKey);
        }
    }
}
