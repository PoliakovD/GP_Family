-- Первичная настройка отдельных учёток приложения в Postgres (ADR-0011).
-- Запускается bootstrap-app-credentials.sh под суперпользователем (POSTGRES_USER) через psql;
-- идемпотентен — повторный запуск безопасен и ничего не ломает.
--
-- Модель ролей:
--   familyhub_owner   NOLOGIN. Владелец схем и всех объектов приложения (identity, medical, kb, audit,
--                     hangfire, public.*). Под него «переключается» приложение — поэтому всё, что
--                     создают EF-миграции и Hangfire, принадлежит ему, а не логин-роли.
--   familyhub_app_a/b Две LOGIN-роли для входа приложения, обе члены familyhub_owner. Ротация паролей
--                     идёт по кругу: работает A — генерируем пароль B, переключаем приложение на B,
--                     отзываем A (и наоборот). Отдельных ролей две, а не одна, потому что смена пароля
--                     единственной роли мгновенно убивает вход у работающего приложения.
--   familyhub_admin   Схема с SECURITY DEFINER-функциями (владелец — суперпользователь). Приложению
--                     нельзя дать CREATEROLE/ADMIN OPTION: роли не могут быть админами друг друга
--                     (круговое членство запрещено), а CREATEROLE позволил бы создавать и менять ЛЮБЫЕ
--                     роли. Функции умеют ровно три вещи и трогают только familyhub_app_a/b.
--
-- Необязательная psql-переменная (задаёт скрипт-обёртка через \set):
--   app_a_password — пароль familyhub_app_a. Без неё роли создаются/чинятся, но пароль не меняется.
--
-- Требуется Postgres >= 16 (GRANT ... WITH INHERIT/SET).

-- ─── Роли ───────────────────────────────────────────────────────────────────────────────────────
DO $roles$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'familyhub_owner') THEN
        CREATE ROLE familyhub_owner NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'familyhub_app_a') THEN
        CREATE ROLE familyhub_app_a NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'familyhub_app_b') THEN
        CREATE ROLE familyhub_app_b NOLOGIN;
    END IF;
END
$roles$;

GRANT familyhub_owner TO familyhub_app_a WITH INHERIT TRUE, SET TRUE;
GRANT familyhub_owner TO familyhub_app_b WITH INHERIT TRUE, SET TRUE;

-- Сессия приложения стартует уже «под» владельцем: current_user = familyhub_owner, а кто вошёл на
-- самом деле (A или B) видно по session_user. Npgsql при возврате соединения в пул делает
-- DISCARD ALL → RESET ROLE возвращает именно это значение по умолчанию, а не логин-роль.
ALTER ROLE familyhub_app_a SET role = 'familyhub_owner';
ALTER ROLE familyhub_app_b SET role = 'familyhub_owner';

-- Создавать схемы (EF-миграции) и расширения (pg_trgm — «trusted», ставится без суперправ) владельцу
-- разрешено на уровне БД; сама БД остаётся во владении суперпользователя.
DO $dbgrants$
BEGIN
    EXECUTE format('GRANT CONNECT, CREATE, TEMP ON DATABASE %I TO familyhub_owner', current_database());
END
$dbgrants$;

-- ─── Передача владения существующих объектов ───────────────────────────────────────────────────
-- REASSIGN OWNED от суперпользователя невозможен (зацепил бы системные объекты), поэтому — по
-- объектам. Всё, что принадлежит расширению (pg_trgm), не трогаем (deptype 'e').
ALTER SCHEMA public OWNER TO familyhub_owner;

DO $ownership$
DECLARE
    r record;
BEGIN
    -- схемы приложения
    FOR r IN
        SELECT nspname FROM pg_namespace
        WHERE nspname NOT IN ('pg_catalog', 'information_schema', 'familyhub_admin', 'public')
          AND nspname NOT LIKE 'pg\_%'
          AND pg_get_userbyid(nspowner) <> 'familyhub_owner'
    LOOP
        EXECUTE format('ALTER SCHEMA %I OWNER TO familyhub_owner', r.nspname);
    END LOOP;

    -- таблицы/вью/матвью/внешние (ALTER TABLE заодно передаёт принадлежащие им последовательности
    -- и identity-колонки)
    FOR r IN
        SELECT n.nspname, c.relname, c.relkind
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'familyhub_admin')
          AND n.nspname NOT LIKE 'pg\_%'
          AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
          AND pg_get_userbyid(c.relowner) <> 'familyhub_owner'
          AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype = 'e')
    LOOP
        EXECUTE format('ALTER %s %I.%I OWNER TO familyhub_owner',
            CASE r.relkind
                WHEN 'v' THEN 'VIEW'
                WHEN 'm' THEN 'MATERIALIZED VIEW'
                WHEN 'f' THEN 'FOREIGN TABLE'
                ELSE 'TABLE'
            END,
            r.nspname, r.relname);
    END LOOP;

    -- отдельно стоящие последовательности (не принадлежащие колонке/identity/расширению)
    FOR r IN
        SELECT n.nspname, c.relname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind = 'S'
          AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'familyhub_admin')
          AND n.nspname NOT LIKE 'pg\_%'
          AND pg_get_userbyid(c.relowner) <> 'familyhub_owner'
          AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype IN ('a', 'i', 'e'))
    LOOP
        EXECUTE format('ALTER SEQUENCE %I.%I OWNER TO familyhub_owner', r.nspname, r.relname);
    END LOOP;

    -- функции / процедуры / агрегаты
    FOR r IN
        SELECT p.oid::regprocedure AS signature, p.prokind
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'familyhub_admin')
          AND n.nspname NOT LIKE 'pg\_%'
          AND pg_get_userbyid(p.proowner) <> 'familyhub_owner'
          AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = p.oid AND d.deptype = 'e')
    LOOP
        EXECUTE format('ALTER %s %s OWNER TO familyhub_owner',
            CASE r.prokind WHEN 'p' THEN 'PROCEDURE' WHEN 'a' THEN 'AGGREGATE' ELSE 'FUNCTION' END,
            r.signature);
    END LOOP;

    -- отдельные типы (enum / domain / range) — типы таблиц следуют за таблицей
    FOR r IN
        SELECT t.oid::regtype AS typ
        FROM pg_type t
        JOIN pg_namespace n ON n.oid = t.typnamespace
        WHERE t.typtype IN ('e', 'd', 'r')
          AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'familyhub_admin')
          AND n.nspname NOT LIKE 'pg\_%'
          AND pg_get_userbyid(t.typowner) <> 'familyhub_owner'
          AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = t.oid AND d.deptype = 'e')
    LOOP
        EXECUTE format('ALTER TYPE %s OWNER TO familyhub_owner', r.typ);
    END LOOP;
END
$ownership$;

-- ─── Функции ротации (единственное, что приложение может делать с ролями) ───────────────────────
-- Схема принадлежит суперпользователю (тому, кто запускает этот файл); функции — SECURITY DEFINER.
CREATE SCHEMA IF NOT EXISTS familyhub_admin;
REVOKE ALL ON SCHEMA familyhub_admin FROM PUBLIC;
GRANT USAGE ON SCHEMA familyhub_admin TO familyhub_owner;

-- Внутренняя проверка цели: только «соседняя» роль приложения и никогда — та, под которой работает
-- вызывающий (приложение не может отключить само себя). Коды ошибок ловит приложение:
-- 42501 — чужая роль, 55006 — роль в использовании.
CREATE OR REPLACE FUNCTION familyhub_admin.assert_sibling(target name) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
BEGIN
    IF target NOT IN ('familyhub_app_a', 'familyhub_app_b') THEN
        RAISE EXCEPTION 'role % is not rotatable', target USING ERRCODE = '42501';
    END IF;
    IF target = session_user THEN
        RAISE EXCEPTION 'role % is in use by this session', target USING ERRCODE = '55006';
    END IF;
END
$fn$;

-- Принимает ТОЛЬКО готовый SCRAM-SHA-256-верификатор, не пароль в открытом виде: открытый пароль
-- не должен попасть ни в лог сервера (при ошибке Postgres логирует текст запроса), ни в
-- pg_stat_statements.
CREATE OR REPLACE FUNCTION familyhub_admin.set_app_role_password(target name, scram_verifier text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
BEGIN
    PERFORM familyhub_admin.assert_sibling(target);
    IF scram_verifier !~ '^SCRAM-SHA-256\$[0-9]+:[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+:[A-Za-z0-9+/=]+$' THEN
        RAISE EXCEPTION 'a pre-hashed SCRAM-SHA-256 verifier is required' USING ERRCODE = '22023';
    END IF;
    EXECUTE format('ALTER ROLE %I WITH LOGIN PASSWORD %L', target, scram_verifier);
END
$fn$;

-- Отзыв: запрет входа, обнуление пароля и обрыв уже открытых соединений (иначе пул приложения,
-- ещё держащий старые соединения, продолжал бы работать под отозванной ролью). Возвращает число
-- оборванных сессий.
CREATE OR REPLACE FUNCTION familyhub_admin.disable_app_role(target name) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
DECLARE
    terminated integer;
BEGIN
    PERFORM familyhub_admin.assert_sibling(target);
    EXECUTE format('ALTER ROLE %I WITH NOLOGIN PASSWORD NULL', target);
    SELECT count(*) FILTER (WHERE pg_terminate_backend(pid)) INTO terminated
    FROM pg_stat_activity
    WHERE usename = target AND pid <> pg_backend_pid();
    RETURN terminated;
END
$fn$;

-- Состояние двух слотов для админ-панели: можно ли войти, есть ли пароль, сколько сессий.
CREATE OR REPLACE FUNCTION familyhub_admin.app_role_status()
RETURNS TABLE (role_name name, can_login boolean, has_password boolean, active_sessions integer)
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
    SELECT a.rolname, a.rolcanlogin, a.rolpassword IS NOT NULL,
           (SELECT count(*)::integer FROM pg_stat_activity s WHERE s.usename = a.rolname)
    FROM pg_authid a
    WHERE a.rolname IN ('familyhub_app_a', 'familyhub_app_b')
    ORDER BY a.rolname
$fn$;

REVOKE ALL ON ALL FUNCTIONS IN SCHEMA familyhub_admin FROM PUBLIC;
GRANT EXECUTE ON FUNCTION
    familyhub_admin.set_app_role_password(name, text),
    familyhub_admin.disable_app_role(name),
    familyhub_admin.app_role_status()
TO familyhub_owner;

-- ─── Итоговая проверка ──────────────────────────────────────────────────────────────────────────
-- Если хоть один объект приложения остался не во владении familyhub_owner, следующая миграция
-- упала бы посреди деплоя с «must be owner of ...». Лучше упасть здесь, при настройке.
DO $verify$
DECLARE
    leftovers text;
BEGIN
    SELECT string_agg(format('%s.%s (%s)', n.nspname, c.relname, pg_get_userbyid(c.relowner)), ', ')
    INTO leftovers
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'familyhub_admin')
      AND n.nspname NOT LIKE 'pg\_%'
      AND c.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
      AND pg_get_userbyid(c.relowner) <> 'familyhub_owner'
      AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype = 'e');

    IF leftovers IS NOT NULL THEN
        RAISE EXCEPTION 'после передачи владения остались объекты не во владении familyhub_owner: %', leftovers;
    END IF;
END
$verify$;

-- ─── Пароль familyhub_app_a (только при первичной настройке) ───────────────────────────────────
\if :{?app_a_password}
ALTER ROLE familyhub_app_a WITH LOGIN PASSWORD :'app_a_password';
\endif
