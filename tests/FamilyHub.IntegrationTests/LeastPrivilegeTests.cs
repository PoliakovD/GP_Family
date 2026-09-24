using System.Net;
using System.Net.Http.Json;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Postgres 17 (как в проде) + настоящие миграции + НАСТОЯЩИЙ deploy/scripts/sql/bootstrap-roles.sql,
/// после чего хост приложения стартует под <c>familyhub_app_a</c>, а не под суперпользователем.
/// Доказывает то, ради чего заведена отдельная учётка (ADR-0011): приложение целиком работает без
/// суперправ, всё, что оно создаёт, принадлежит роли-владельцу, а ротация паролей возможна ТОЛЬКО
/// через три SECURITY DEFINER-функции и только для двух ролей приложения.
/// </summary>
public class LeastPrivilegeWebFactory : FamilyHubWebFactory
{
    public const string AppRoleA = "familyhub_app_a";
    public const string AppRoleB = "familyhub_app_b";
    public const string OwnerRole = "familyhub_owner";
    public const string AppAPassword = "least-privilege-test-password-A1";

    private PostgreSqlContainer? _pg;

    // Как в проде (deploy/docker-compose.prod.yml). Константа — вызывается из базового конструктора.
    protected override string PostgresImage => "postgres:17-alpine";

    protected override string HostPostgresConnectionString => ConnectionAs(AppRoleA, AppAPassword);

    protected override async Task AfterMigrationsAsync(PostgreSqlContainer postgres)
    {
        _pg = postgres;
        var result = await RunBootstrapAsync(AppAPassword);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"bootstrap-roles.sql завершился с кодом {result.ExitCode}: {result.Stderr}");
    }

    /// <summary>pooled=false — для проверок «вход отклонён»: Npgsql отдаёт соединение из пула БЕЗ
    /// повторной проверки пароля, так что с пулом отозванный пароль «проходил» бы, пока живо
    /// соединение (ровно поэтому disable_app_role рвёт открытые сессии).</summary>
    public string ConnectionAs(string user, string? password, bool pooled = true)
    {
        var builder = new NpgsqlConnectionStringBuilder(_pg!.GetConnectionString()) { Username = user, Pooling = pooled };
        if (password is null) builder.Remove("Password"); else builder.Password = password;
        return builder.ConnectionString;
    }

    public string SuperuserConnection => _pg!.GetConnectionString();

    /// <summary>Прогоняет реальный SQL через psql в контейнере — как это делает bootstrap-app-credentials.sh
    /// (там же psql читает файл со stdin). Пароль — тем же путём: строкой \set перед файлом.</summary>
    public async Task<ExecResult> RunBootstrapAsync(string? appAPassword)
    {
        var sql = await File.ReadAllTextAsync(FindRepoFile("deploy/scripts/sql/bootstrap-roles.sql"));
        var script = (appAPassword is null ? "" : $"\\set app_a_password '{appAPassword}'\n") + sql;
        await _pg!.CopyAsync(System.Text.Encoding.UTF8.GetBytes(script), "/tmp/bootstrap-roles.sql");
        return await _pg.ExecAsync(["psql", "-v", "ON_ERROR_STOP=1", "-q", "-U", "postgres", "-d", "familyhub_test", "-f", "/tmp/bootstrap-roles.sql"]);
    }

    private static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{relative} не найден выше {AppContext.BaseDirectory}");
    }
}

public class LeastPrivilegeTests(LeastPrivilegeWebFactory factory) : IClassFixture<LeastPrivilegeWebFactory>
{
    private sealed record ConsentVersion(string Version);

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>SCRAM-верификатор пароля — тем же способом, каким его хранит сам Postgres: временная
    /// роль с этим паролем, читаем rolpassword из pg_authid, роль удаляем. Так тесту не нужна
    /// собственная реализация SCRAM.</summary>
    private async Task<string> ScramVerifierAsync(string password)
    {
        var role = $"lp_tmp_{Guid.NewGuid():N}";
        await using var su = await OpenAsync(factory.SuperuserConnection);
        await ExecuteAsync(su, $"SET password_encryption = 'scram-sha-256'; CREATE ROLE {role} PASSWORD '{password}'");
        var verifier = await ScalarAsync<string>(su, $"SELECT rolpassword FROM pg_authid WHERE rolname = '{role}'");
        await ExecuteAsync(su, $"DROP ROLE {role}");
        return verifier!;
    }

    /// <summary>Возвращает ролям B в исходное состояние (NOLOGIN, без пароля) — тесты не должны
    /// оставлять следов друг для друга.</summary>
    private async Task ResetRoleBAsync()
    {
        await using var su = await OpenAsync(factory.SuperuserConnection);
        await ExecuteAsync(su, $"ALTER ROLE {LeastPrivilegeWebFactory.AppRoleB} WITH NOLOGIN PASSWORD NULL");
    }

    private async Task<PostgresException> ExpectPostgresErrorAsync(Func<Task> action)
    {
        var thrown = await Assert.ThrowsAnyAsync<PostgresException>(action);
        return thrown;
    }

    // ─── Приложение целиком под ролью приложения ────────────────────────────────────────────────

    [Fact]
    public async Task Host_RunsUnderAppRole_AndServesRequestsThatReadAndWriteTheDatabase()
    {
        var client = factory.CreateClientAs(910_001);
        // Без принятого согласия ПДн данные недоступны (403 consent_required) — принимаем, как реальный пользователь.
        var consent = await client.GetFromJsonAsync<ConsentVersion>("/api/consents/current");
        (await client.PostAsJsonAsync("/api/consents/accept", new { version = consent!.Version })).EnsureSuccessStatusCode();

        var response = await client.GetAsync("/api/home/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        // Запрос выше — запись (пользователь по dev-аутентификации, согласие) и чтение под ролью
        // приложения; хосту суперпользовательские креды не выдавались вообще, так что успешный
        // ответ уже значит «приложение работает без суперправ». Дополнительно — видим его сессии.
        await using var su = await OpenAsync(factory.SuperuserConnection);
        var appSessions = await ScalarAsync<long>(su,
            "SELECT count(*) FROM pg_stat_activity WHERE usename = 'familyhub_app_a' AND datname = 'familyhub_test'");
        appSessions.Should().BeGreaterThan(0, "хост стартовал под ролью приложения");
    }

    [Fact]
    public async Task Objects_CreatedByTheHostItself_AreOwnedByOwnerRole()
    {
        // Схему hangfire на этом Postgres создаёт сам хост (Hangfire ставит её при первом старте)
        // под familyhub_app_a — она должна принадлежать владельцу, а не логин-роли.
        await factory.CreateClientAs(910_002).GetAsync("/api/home/summary");

        await using var su = await OpenAsync(factory.SuperuserConnection);
        var hangfireOwner = await ScalarAsync<string>(su, "SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'hangfire'");
        var wrongOwner = await ScalarAsync<long>(su,
            "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname IN ('hangfire') AND pg_get_userbyid(c.relowner) <> 'familyhub_owner'");

        hangfireOwner.Should().Be(LeastPrivilegeWebFactory.OwnerRole);
        wrongOwner.Should().Be(0);
    }

    // ─── Модель ролей ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AppConnection_SessionUserIsAppRole_CurrentUserIsOwner_AlsoAfterPoolReset()
    {
        // Maximum Pool Size=1 заставляет Npgsql вернуть в пул и снова отдать ТО ЖЕ физическое
        // соединение — между ними Npgsql делает DISCARD ALL (RESET ROLE). Роль должна вернуться к
        // значению из ALTER ROLE ... SET role, а не к логин-роли, иначе объекты, созданные после
        // первого возврата в пул, достались бы familyhub_app_a вместо владельца.
        var pooled = new NpgsqlConnectionStringBuilder(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword))
        { MaxPoolSize = 1, MinPoolSize = 0 }.ConnectionString;

        for (var i = 0; i < 3; i++)
        {
            await using var connection = await OpenAsync(pooled);
            (await ScalarAsync<string>(connection, "SELECT session_user")).Should().Be(LeastPrivilegeWebFactory.AppRoleA);
            (await ScalarAsync<string>(connection, "SELECT current_user")).Should().Be(LeastPrivilegeWebFactory.OwnerRole, $"открытие №{i + 1}");
        }
    }

    [Fact]
    public async Task ObjectCreatedByAppRole_IsOwnedByOwner_NotByLoginRole()
    {
        await using var connection = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword));
        await ExecuteAsync(connection, "CREATE TABLE public.lp_probe (id int primary key generated always as identity)");
        try
        {
            var owner = await ScalarAsync<string>(connection, "SELECT tableowner FROM pg_tables WHERE tablename = 'lp_probe'");
            var sequenceOwner = await ScalarAsync<string>(connection, "SELECT sequenceowner FROM pg_sequences WHERE sequencename = 'lp_probe_id_seq'");

            owner.Should().Be(LeastPrivilegeWebFactory.OwnerRole);
            sequenceOwner.Should().Be(LeastPrivilegeWebFactory.OwnerRole);
        }
        finally
        {
            await ExecuteAsync(connection, "DROP TABLE public.lp_probe");
        }
    }

    [Fact]
    public async Task AfterBootstrap_EveryApplicationObject_IsOwnedByOwnerRole()
    {
        await using var su = await OpenAsync(factory.SuperuserConnection);

        var strayRelations = await ScalarAsync<long>(su,
            "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'familyhub_admin') AND n.nspname NOT LIKE 'pg\\_%' " +
            "AND c.relkind IN ('r','p','v','m','f','S') AND pg_get_userbyid(c.relowner) <> 'familyhub_owner' " +
            "AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype = 'e')");
        var straySchemas = await ScalarAsync<long>(su,
            "SELECT count(*) FROM pg_namespace WHERE nspname IN ('identity','medical','kb','audit','public') " +
            "AND pg_get_userbyid(nspowner) <> 'familyhub_owner'");
        var appTables = await ScalarAsync<long>(su,
            "SELECT count(*) FROM pg_tables WHERE schemaname IN ('identity','medical','kb','audit')");

        appTables.Should().BeGreaterThan(20, "миграции должны были создать схему приложения — иначе тест ничего не доказывает");
        strayRelations.Should().Be(0);
        straySchemas.Should().Be(0);
    }

    [Fact]
    public async Task Bootstrap_IsIdempotent_KeepsPasswordWhenNoneGiven_AndChangesItWhenGiven()
    {
        // Повторный запуск без пароля: не падает, пароль A не меняется.
        var again = await factory.RunBootstrapAsync(appAPassword: null);
        again.ExitCode.Should().Be(0, again.Stderr);
        await using (var stillWorks = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword)))
            (await ScalarAsync<int>(stillWorks, "SELECT 1")).Should().Be(1);

        // С новым паролем (--force в скрипте): меняется, старый перестаёт работать. Возвращаем как было.
        const string rotated = "rotated-by-force-password-Z9";
        (await factory.RunBootstrapAsync(rotated)).ExitCode.Should().Be(0);
        try
        {
            await using var withNew = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, rotated));
            (await ScalarAsync<int>(withNew, "SELECT 1")).Should().Be(1);
            var ex = await ExpectPostgresErrorAsync(async () =>
                await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword, pooled: false)));
            ex.SqlState.Should().Be("28P01", "старый пароль больше не подходит");
        }
        finally
        {
            (await factory.RunBootstrapAsync(LeastPrivilegeWebFactory.AppAPassword)).ExitCode.Should().Be(0);
        }
    }

    [Fact]
    public async Task AppRole_CannotManageRolesDirectly()
    {
        await using var connection = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword));

        (await ExpectPostgresErrorAsync(() => ExecuteAsync(connection, "ALTER ROLE familyhub_app_b PASSWORD 'x'"))).SqlState.Should().Be("42501");
        (await ExpectPostgresErrorAsync(() => ExecuteAsync(connection, "CREATE ROLE lp_intruder LOGIN"))).SqlState.Should().Be("42501");
        (await ExpectPostgresErrorAsync(() => ExecuteAsync(connection, "ALTER ROLE postgres PASSWORD 'x'"))).SqlState.Should().Be("42501");
    }

    // ─── Функции ротации ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RotationFunctions_AreNotCallableByOutsiders()
    {
        await using (var su = await OpenAsync(factory.SuperuserConnection))
            await ExecuteAsync(su, "DROP ROLE IF EXISTS lp_outsider; CREATE ROLE lp_outsider LOGIN PASSWORD 'outsider-pw'");
        try
        {
            await using var outsider = await OpenAsync(factory.ConnectionAs("lp_outsider", "outsider-pw"));

            var ex = await ExpectPostgresErrorAsync(() => ScalarAsync<object>(outsider, "SELECT * FROM familyhub_admin.app_role_status()"));

            ex.SqlState.Should().Be("42501", "у PUBLIC нет ни USAGE на схему, ни EXECUTE на функции");
        }
        finally
        {
            await using var su = await OpenAsync(factory.SuperuserConnection);
            await ExecuteAsync(su, "DROP ROLE IF EXISTS lp_outsider");
        }
    }

    [Fact]
    public async Task SetPassword_ForSiblingRole_LetsItLogIn_AndRejectsPlaintextAndForeignRoles()
    {
        var verifier = await ScramVerifierAsync("sibling-password-B1");
        await using var app = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword));
        try
        {
            // B ещё не может войти…
            (await ExpectPostgresErrorAsync(async () => await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleB, "sibling-password-B1", pooled: false))))
                .SqlState.Should().BeOneOf("28000", "28P01");

            // …а после смены пароля приложением (тем самым вызовом, что сделает админ-панель) — может.
            await ExecuteAsync(app, "SELECT familyhub_admin.set_app_role_password('familyhub_app_b', @v)", ("v", verifier));
            await using (var asB = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleB, "sibling-password-B1")))
            {
                (await ScalarAsync<string>(asB, "SELECT session_user")).Should().Be(LeastPrivilegeWebFactory.AppRoleB);
                (await ScalarAsync<string>(asB, "SELECT current_user")).Should().Be(LeastPrivilegeWebFactory.OwnerRole);
            }

            // Открытый пароль не принимается — он мог бы попасть в лог сервера при ошибке.
            (await ExpectPostgresErrorAsync(() => ExecuteAsync(app, "SELECT familyhub_admin.set_app_role_password('familyhub_app_b', 'plaintext-password')")))
                .SqlState.Should().Be("22023");

            // Чужие роли — нельзя, включая суперпользователя.
            (await ExpectPostgresErrorAsync(() => ExecuteAsync(app, "SELECT familyhub_admin.set_app_role_password('postgres', @v)", ("v", verifier))))
                .SqlState.Should().Be("42501");
            (await ExpectPostgresErrorAsync(() => ExecuteAsync(app, "SELECT familyhub_admin.set_app_role_password('familyhub_owner', @v)", ("v", verifier))))
                .SqlState.Should().Be("42501");
        }
        finally
        {
            await ResetRoleBAsync();
        }
    }

    [Fact]
    public async Task ApplicationCannotDisableTheRoleItIsRunningAs()
    {
        await using var app = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword));

        var ex = await ExpectPostgresErrorAsync(() => ExecuteAsync(app, "SELECT familyhub_admin.disable_app_role('familyhub_app_a')"));
        ex.SqlState.Should().Be("55006");

        // и сама роль осталась рабочей
        (await ScalarAsync<int>(app, "SELECT 1")).Should().Be(1);
    }

    [Fact]
    public async Task DisableAppRole_BlocksLogin_ResetsPassword_AndTerminatesOpenSessions()
    {
        var verifier = await ScramVerifierAsync("to-be-revoked-B2");
        await using var app = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword));
        try
        {
            await ExecuteAsync(app, "SELECT familyhub_admin.set_app_role_password('familyhub_app_b', @v)", ("v", verifier));
            await using var oldPoolConnection = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleB, "to-be-revoked-B2"));
            (await ScalarAsync<int>(oldPoolConnection, "SELECT 1")).Should().Be(1);

            var terminated = await ScalarAsync<int>(app, "SELECT familyhub_admin.disable_app_role('familyhub_app_b')");

            terminated.Should().BeGreaterThanOrEqualTo(1, "уже открытое соединение старой роли (пул приложения) должно быть оборвано");
            // Оборванное соединение больше не отвечает.
            await Assert.ThrowsAnyAsync<Exception>(() => ScalarAsync<int>(oldPoolConnection, "SELECT 1"));
            // Новый вход невозможен.
            (await ExpectPostgresErrorAsync(async () => await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleB, "to-be-revoked-B2", pooled: false))))
                .SqlState.Should().BeOneOf("28000", "28P01");
        }
        finally
        {
            await ResetRoleBAsync();
        }
    }

    [Fact]
    public async Task AppRoleStatus_ReportsBothSlots()
    {
        await using var app = await OpenAsync(factory.ConnectionAs(LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppAPassword));
        await using var command = new NpgsqlCommand(
            "SELECT role_name::text, can_login, has_password, active_sessions FROM familyhub_admin.app_role_status()", app);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new Dictionary<string, (bool CanLogin, bool HasPassword, int Sessions)>();
        while (await reader.ReadAsync()) rows[reader.GetString(0)] = (reader.GetBoolean(1), reader.GetBoolean(2), reader.GetInt32(3));

        rows.Keys.Should().BeEquivalentTo([LeastPrivilegeWebFactory.AppRoleA, LeastPrivilegeWebFactory.AppRoleB]);
        rows[LeastPrivilegeWebFactory.AppRoleA].Should().Match<(bool CanLogin, bool HasPassword, int Sessions)>(r => r.CanLogin && r.HasPassword && r.Sessions >= 1);
        rows[LeastPrivilegeWebFactory.AppRoleB].Should().Match<(bool CanLogin, bool HasPassword, int Sessions)>(r => !r.CanLogin && !r.HasPassword && r.Sessions == 0);
    }
}
