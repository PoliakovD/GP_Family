#!/usr/bin/env bash
# Первичная настройка ОТДЕЛЬНЫХ учёток приложения (ADR-0011) — запускается ОДИН раз на VPS ДО деплоя
# версии, которая требует переменные DB_APP_*/MINIO_APP_*, и (опционально) на dev-стеке.
#
#   Postgres: приложение перестаёт ходить под суперпользователем. Создаются роль-владелец схем
#             familyhub_owner и две логин-роли familyhub_app_a/b (для ротации без простоя), владение
#             существующими объектами передаётся владельцу (см. sql/bootstrap-roles.sql).
#   MinIO:    приложение перестаёт ходить под root. Создаётся пользователь familyhub-app с политикой
#             только на бакет приложения и его service account (access key + secret) — под ним и
#             работает приложение; ротация = выпуск нового service account рядом со старым.
#
# Суперпользователь Postgres и root MinIO остаются — для бэкапов и ручного администрирования, их
# ротирует человек отдельными скриптами (rotate-postgres-superuser.sh, rotate-minio-root.sh).
#
# Использование (на VPS, из /opt/familyhub):
#   bash scripts/bootstrap-app-credentials.sh
# Dev-стек (из корня репозитория, .env рядом с docker-compose.yml):
#   bash deploy/scripts/bootstrap-app-credentials.sh --dev
# Изолированный прогон/тест (отдельные compose-проект и env-файл):
#   bash deploy/scripts/bootstrap-app-credentials.sh --dev --env-file /tmp/x.env --project-name fhtest
#
# Секреты печатаются ТОЛЬКО в stdout в конце (все сообщения — в stderr) и нигде не сохраняются:
# скопируйте четыре строки в PROD_ENV (GitHub Secret) сразу. Пароли идут в контейнеры через stdin,
# а не аргументами командной строки — не видны в `ps`.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SQL_FILE="$SCRIPT_DIR/sql/bootstrap-roles.sql"

COMPOSE_FILE="docker-compose.yml"
ENV_FILE=".env"
PROJECT_NAME=""
FORCE=0

while [ $# -gt 0 ]; do
    case "$1" in
        --dev)
            # Из корня репозитория (или откуда угодно — пути от расположения скрипта).
            COMPOSE_FILE="$SCRIPT_DIR/../../docker-compose.yml"
            ENV_FILE="$SCRIPT_DIR/../../.env"
            ;;
        --compose-file) COMPOSE_FILE="$2"; shift ;;
        --env-file) ENV_FILE="$2"; shift ;;
        --project-name) PROJECT_NAME="$2"; shift ;;
        --force) FORCE=1 ;;
        -h|--help) sed -n '2,25p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "Неизвестный аргумент: $1" >&2; exit 2 ;;
    esac
    shift
done

log() { echo "[bootstrap] $*" >&2; }
die() { echo "[bootstrap] ОШИБКА: $*" >&2; exit 1; }

[ -f "$COMPOSE_FILE" ] || die "не найден compose-файл: $COMPOSE_FILE"
[ -f "$ENV_FILE" ] || die "не найден env-файл: $ENV_FILE"
[ -f "$SQL_FILE" ] || die "не найден $SQL_FILE"
command -v openssl >/dev/null || die "нужен openssl"

# Прод-compose требует DB_APP_*/MINIO_APP_* (${VAR:?}) ещё до того, как они созданы — compose
# интерполирует ВЕСЬ файл на любой команде. Заглушки только для этого процесса, в .env не пишутся.
export DB_APP_USER="${DB_APP_USER:-bootstrap}" DB_APP_PASSWORD="${DB_APP_PASSWORD:-bootstrap}"
export MINIO_APP_ACCESS_KEY="${MINIO_APP_ACCESS_KEY:-bootstrap}" MINIO_APP_SECRET_KEY="${MINIO_APP_SECRET_KEY:-bootstrap}"

dc() {
    if [ -n "$PROJECT_NAME" ]; then
        docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" -p "$PROJECT_NAME" "$@"
    else
        docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"
    fi
}

# Значение ключа из env-файла (последнее вхождение, без кавычек/CR). Не `source` — .env содержит
# base64 и спецсимволы, которые shell истолковал бы по-своему.
envval() {
    local v
    v="$(grep -E "^$1=" "$ENV_FILE" | tail -n 1 | cut -d= -f2- | tr -d '\r' || true)"
    v="${v%\"}"; v="${v#\"}"; v="${v%\'}"; v="${v#\'}"
    printf '%s' "$v"
}

# Экранирование для вставки в одинарные кавычки shell-скрипта, уходящего в контейнер.
shq() { printf "'%s'" "${1//\'/\'\\\'\'}"; }

# Случайная строка из заданного набора символов БЕЗ `tr | head` (под pipefail head рвёт конвейер
# по SIGPIPE, и скрипт падает с кодом 141) — набираем из base64 порциями.
rand_chars() {
    local n="$1" charset="$2" out=""
    while [ "${#out}" -lt "$n" ]; do
        out+="$(openssl rand -base64 48 | tr -dc "$charset")"
    done
    printf '%s' "${out:0:$n}"
}

PG_SUPERUSER="$(envval POSTGRES_USER)"
PG_DB="$(envval POSTGRES_DB)"
MINIO_ROOT_USER="$(envval MINIO_ROOT_USER)"
MINIO_ROOT_PASSWORD="$(envval MINIO_ROOT_PASSWORD)"
MINIO_BUCKET="$(envval MINIO_BUCKET)"; MINIO_BUCKET="${MINIO_BUCKET:-familyhub}"
[ -n "$PG_SUPERUSER" ] && [ -n "$PG_DB" ] || die "в $ENV_FILE не заданы POSTGRES_USER/POSTGRES_DB"
[ -n "$MINIO_ROOT_USER" ] && [ -n "$MINIO_ROOT_PASSWORD" ] || die "в $ENV_FILE не заданы MINIO_ROOT_USER/MINIO_ROOT_PASSWORD"

if [ "$FORCE" -eq 0 ] && { [ -n "$(envval DB_APP_USER)" ] || [ -n "$(envval MINIO_APP_ACCESS_KEY)" ]; }; then
    die "в $ENV_FILE уже заданы DB_APP_USER/MINIO_APP_ACCESS_KEY — настройка выполнена. Повторный запуск с --force выпустит НОВЫЕ пароль БД и ключ MinIO (старый ключ MinIO останется действующим, пока его не отозвать)."
fi

log "Проверяю, что postgres и minio запущены..."
dc exec -T postgres pg_isready -U "$PG_SUPERUSER" -d "$PG_DB" >/dev/null || die "postgres не отвечает (docker compose up -d postgres)"
dc exec -T minio mc --version >/dev/null 2>&1 || die "minio не запущен или в его образе нет mc"

# ─── Postgres ───────────────────────────────────────────────────────────────────────────────────
DB_APP_USER_NEW="familyhub_app_a"
# base64url: без '+', '/', '=' — не ломает ни строку подключения (';'), ни compose-интерполяцию ('$').
DB_APP_PASSWORD_NEW="$(rand_chars 43 'A-Za-z0-9_-')"

log "Postgres: роли, владение объектами, функции ротации..."
{
    printf "\\\\set app_a_password '%s'\n" "$DB_APP_PASSWORD_NEW"
    cat "$SQL_FILE"
} | dc exec -T postgres psql -v ON_ERROR_STOP=1 -q -U "$PG_SUPERUSER" -d "$PG_DB" >/dev/null

# ─── MinIO ──────────────────────────────────────────────────────────────────────────────────────
MINIO_APP_ACCESS_KEY_NEW="FHAPP$(rand_chars 15 'A-Z0-9')"
MINIO_APP_SECRET_KEY_NEW="$(rand_chars 40 'A-Za-z0-9')"
# Пароль самого пользователя familyhub-app не нужен никому (входит приложение под service account) —
# генерируется и отбрасывается.
MINIO_APP_USER_PASSWORD="$(rand_chars 32 'A-Za-z0-9')"

log "MinIO: бакет, политика, пользователь, service account..."
# Скрипт для контейнера идёт через stdin (креды не попадают в argv). Политика — только на бакет
# приложения, без административных действий и без создания бакетов (бакет заводит этот скрипт).
dc exec -T minio sh -s >/dev/null <<MINIO_SCRIPT
set -eu
export MC_CONFIG_DIR=/tmp/mc-bootstrap
trap 'rm -rf /tmp/mc-bootstrap /tmp/fh-policy.json' EXIT
mc alias set local http://localhost:9000 $(shq "$MINIO_ROOT_USER") $(shq "$MINIO_ROOT_PASSWORD") >/dev/null

mc mb --ignore-existing local/$MINIO_BUCKET >/dev/null

cat > /tmp/fh-policy.json <<'POLICY'
{"Version":"2012-10-17","Statement":[
 {"Effect":"Allow","Action":["s3:GetBucketLocation","s3:ListBucket","s3:ListBucketMultipartUploads"],"Resource":["arn:aws:s3:::$MINIO_BUCKET"]},
 {"Effect":"Allow","Action":["s3:GetObject","s3:PutObject","s3:DeleteObject","s3:AbortMultipartUpload","s3:ListMultipartUploadParts"],"Resource":["arn:aws:s3:::$MINIO_BUCKET/*"]}
]}
POLICY

# mc >= 2022-12: policy create/attach; старые mc — policy add/set. Пробуем новый синтаксис первым.
mc admin policy create local familyhub-app /tmp/fh-policy.json >/dev/null 2>&1 \
    || mc admin policy add local familyhub-app /tmp/fh-policy.json >/dev/null

mc admin user info local familyhub-app >/dev/null 2>&1 \
    || mc admin user add local familyhub-app $(shq "$MINIO_APP_USER_PASSWORD") >/dev/null

mc admin policy attach local familyhub-app --user familyhub-app >/dev/null 2>&1 \
    || mc admin policy set local familyhub-app user=familyhub-app >/dev/null

mc admin user svcacct add local familyhub-app \
    --access-key $(shq "$MINIO_APP_ACCESS_KEY_NEW") --secret-key $(shq "$MINIO_APP_SECRET_KEY_NEW") \
    --name familyhub-app >/dev/null
MINIO_SCRIPT

log "Готово. Ниже — строки для PROD_ENV (GitHub Secret) / .env. Они показываются ОДИН раз."
log "Затем: задеплойте версию, использующую DB_APP_*/MINIO_APP_* (см. deploy/README.md, «Учётки приложения»)."
cat <<ENV_LINES
DB_APP_USER=$DB_APP_USER_NEW
DB_APP_PASSWORD=$DB_APP_PASSWORD_NEW
MINIO_APP_ACCESS_KEY=$MINIO_APP_ACCESS_KEY_NEW
MINIO_APP_SECRET_KEY=$MINIO_APP_SECRET_KEY_NEW
ENV_LINES
