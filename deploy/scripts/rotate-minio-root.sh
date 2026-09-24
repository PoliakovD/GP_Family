#!/usr/bin/env bash
# Смена пароля ROOT MinIO (MINIO_ROOT_PASSWORD) — оператором, руками (ADR-0011).
#
# Приложение под root не работает (оно ходит под service account пользователя familyhub-app),
# поэтому смена root-пароля НЕ ломает приложение. Root нужен только для бэкапа (контейнер backup:
# mc mirror), консоли MinIO по WireGuard и bootstrap-app-credentials.sh.
#
# В отличие от Postgres, пароль root MinIO не хранится в самой БД MinIO — сервер берёт его из
# переменных окружения при старте. Поэтому «смена» = новое значение в PROD_ENV + перезапуск minio:
#
#   prepare  — сгенерировать новый пароль и напечатать строку MINIO_ROOT_PASSWORD=... + шаги.
#              Ничего не меняет.
#   verify   — ПОСЛЕ деплоя: проверить, что root входит с паролем из env-файла, а ключ приложения
#              (MINIO_APP_ACCESS_KEY/SECRET_KEY) продолжает работать с бакетом.
#
# Порядок:
#   1. bash scripts/rotate-minio-root.sh prepare        → взять MINIO_ROOT_PASSWORD=...
#   2. Обновить MINIO_ROOT_PASSWORD в PROD_ENV (GitHub Secret) и задеплоить — minio и backup
#      пересоздадутся с новым значением (кратковременная недоступность хранилища — секунды).
#   3. bash scripts/rotate-minio-root.sh verify
# Старые версии MinIO при смене root-кредов могли требовать MINIO_ROOT_USER_OLD/
# MINIO_ROOT_PASSWORD_OLD для перешифровки конфигурации — если после деплоя `verify` не проходит,
# см. документацию используемой версии.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="docker-compose.yml"
ENV_FILE=".env"
PROJECT_NAME=""
COMMAND=""

while [ $# -gt 0 ]; do
    case "$1" in
        prepare|verify) COMMAND="$1" ;;
        --dev) COMPOSE_FILE="$SCRIPT_DIR/../../docker-compose.yml"; ENV_FILE="$SCRIPT_DIR/../../.env" ;;
        --compose-file) COMPOSE_FILE="$2"; shift ;;
        --env-file) ENV_FILE="$2"; shift ;;
        --project-name) PROJECT_NAME="$2"; shift ;;
        -h|--help) sed -n '2,25p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "Неизвестный аргумент: $1" >&2; exit 2 ;;
    esac
    shift
done
[ -n "$COMMAND" ] || { echo "Укажите команду: prepare | verify (см. --help)" >&2; exit 2; }

log() { echo "[rotate-minio] $*" >&2; }
die() { echo "[rotate-minio] ОШИБКА: $*" >&2; exit 1; }

command -v openssl >/dev/null || die "нужен openssl"

rand_chars() {
    local n="$1" charset="$2" out=""
    while [ "${#out}" -lt "$n" ]; do out+="$(openssl rand -base64 48 | tr -dc "$charset")"; done
    printf '%s' "${out:0:$n}"
}

if [ "$COMMAND" = "prepare" ]; then
    log "Новый пароль root MinIO сгенерирован (в MinIO пока НЕ применён). Дальше:"
    log "  1. обновите MINIO_ROOT_PASSWORD в PROD_ENV этим значением;"
    log "  2. задеплойте (minio и backup пересоздадутся);"
    log "  3. bash scripts/rotate-minio-root.sh verify"
    echo "MINIO_ROOT_PASSWORD=$(rand_chars 32 'A-Za-z0-9')"
    exit 0
fi

# ─── verify ─────────────────────────────────────────────────────────────────────────────────────
[ -f "$COMPOSE_FILE" ] || die "не найден compose-файл: $COMPOSE_FILE"
[ -f "$ENV_FILE" ] || die "не найден env-файл: $ENV_FILE"

export DB_APP_USER="${DB_APP_USER:-x}" DB_APP_PASSWORD="${DB_APP_PASSWORD:-x}"
export MINIO_APP_ACCESS_KEY="${MINIO_APP_ACCESS_KEY:-x}" MINIO_APP_SECRET_KEY="${MINIO_APP_SECRET_KEY:-x}"

dc() {
    if [ -n "$PROJECT_NAME" ]; then
        docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" -p "$PROJECT_NAME" "$@"
    else
        docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"
    fi
}

envval() {
    local v
    v="$(grep -E "^$1=" "$ENV_FILE" | tail -n 1 | cut -d= -f2- | tr -d '\r' || true)"
    v="${v%\"}"; v="${v#\"}"; v="${v%\'}"; v="${v#\'}"
    printf '%s' "$v"
}
shq() { printf "'%s'" "${1//\'/\'\\\'\'}"; }

ROOT_USER="$(envval MINIO_ROOT_USER)"
ROOT_PASSWORD="$(envval MINIO_ROOT_PASSWORD)"
APP_KEY="$(envval MINIO_APP_ACCESS_KEY)"
APP_SECRET="$(envval MINIO_APP_SECRET_KEY)"
BUCKET="$(envval MINIO_BUCKET)"; BUCKET="${BUCKET:-familyhub}"
[ -n "$ROOT_USER" ] && [ -n "$ROOT_PASSWORD" ] || die "в $ENV_FILE не заданы MINIO_ROOT_USER/MINIO_ROOT_PASSWORD"

log "Проверяю вход root (пароль из $ENV_FILE) и ключ приложения..."
dc exec -T minio sh -s <<VERIFY_SCRIPT
set -eu
export MC_CONFIG_DIR=/tmp/mc-verify
trap 'rm -rf /tmp/mc-verify' EXIT
if mc alias set root http://localhost:9000 $(shq "$ROOT_USER") $(shq "$ROOT_PASSWORD") >/dev/null 2>&1 && mc admin info root >/dev/null 2>&1; then
    echo "OK    root входит с паролем из $ENV_FILE"
else
    echo "FAIL  root НЕ входит с паролем из $ENV_FILE — minio запущен со старым значением? (деплой выполнен?)"; exit 1
fi
if [ -n $(shq "$APP_KEY") ]; then
    mc alias set app http://localhost:9000 $(shq "$APP_KEY") $(shq "$APP_SECRET") >/dev/null 2>&1
    if mc ls app/$BUCKET >/dev/null 2>&1; then
        echo "OK    ключ приложения работает с бакетом $BUCKET"
    else
        echo "FAIL  ключ приложения не может читать бакет $BUCKET"; exit 1
    fi
else
    echo "SKIP  MINIO_APP_ACCESS_KEY не задан в $ENV_FILE — ключ приложения не проверялся"
fi
VERIFY_SCRIPT
