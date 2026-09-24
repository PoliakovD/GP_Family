#!/usr/bin/env bash
# Смена пароля СУПЕРПОЛЬЗОВАТЕЛЯ Postgres (POSTGRES_USER) — оператором, руками (ADR-0011).
#
# Приложение под суперпользователем не работает (оно ходит под familyhub_app_a/b), поэтому смена
# этого пароля НЕ затрагивает приложение и происходит без простоя. Он нужен только для бэкапа
# (контейнер backup), DBeaver/psql по WireGuard и разового bootstrap-app-credentials.sh.
#
# Что делает: генерирует новый пароль, меняет его в БД (ALTER ROLE), печатает строку
# POSTGRES_PASSWORD=... в stdout. Ничего, кроме БД, сам не меняет — если не указан --write-env.
#
# ПОСЛЕ запуска сразу:
#   1. Обновить POSTGRES_PASSWORD в PROD_ENV (GitHub Secret) — источник правды. Следующий деплой
#      перезапишет /opt/familyhub/.env из PROD_ENV; старое значение там вернуло бы старый пароль.
#   2. Задеплоить (пересоздаст backup с новым паролем). Пока этого не сделано, ночной бэкап
#      (03:30) и клиенты со старым паролем получают «password authentication failed».
#   3. Обновить сохранённый пароль в DBeaver.
# --write-env дополнительно правит POSTGRES_PASSWORD в env-файле на сервере, чтобы
# `docker compose up -d backup` сразу подхватил новый пароль до деплоя.
#
#   bash scripts/rotate-postgres-superuser.sh [--write-env]                    # на VPS, из /opt/familyhub
#   bash deploy/scripts/rotate-postgres-superuser.sh --dev [--write-env]       # dev-стек
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="docker-compose.yml"
ENV_FILE=".env"
PROJECT_NAME=""
WRITE_ENV=0

while [ $# -gt 0 ]; do
    case "$1" in
        --dev) COMPOSE_FILE="$SCRIPT_DIR/../../docker-compose.yml"; ENV_FILE="$SCRIPT_DIR/../../.env" ;;
        --compose-file) COMPOSE_FILE="$2"; shift ;;
        --env-file) ENV_FILE="$2"; shift ;;
        --project-name) PROJECT_NAME="$2"; shift ;;
        --write-env) WRITE_ENV=1 ;;
        -h|--help) sed -n '2,24p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "Неизвестный аргумент: $1" >&2; exit 2 ;;
    esac
    shift
done

log() { echo "[rotate-pg] $*" >&2; }
die() { echo "[rotate-pg] ОШИБКА: $*" >&2; exit 1; }

[ -f "$COMPOSE_FILE" ] || die "не найден compose-файл: $COMPOSE_FILE"
[ -f "$ENV_FILE" ] || die "не найден env-файл: $ENV_FILE"
command -v openssl >/dev/null || die "нужен openssl"

# См. bootstrap-app-credentials.sh: прод-compose требует эти переменные на любой команде.
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

rand_chars() {
    local n="$1" charset="$2" out=""
    while [ "${#out}" -lt "$n" ]; do out+="$(openssl rand -base64 48 | tr -dc "$charset")"; done
    printf '%s' "${out:0:$n}"
}

PG_SUPERUSER="$(envval POSTGRES_USER)"
PG_DB="$(envval POSTGRES_DB)"
[ -n "$PG_SUPERUSER" ] && [ -n "$PG_DB" ] || die "в $ENV_FILE не заданы POSTGRES_USER/POSTGRES_DB"

dc exec -T postgres pg_isready -U "$PG_SUPERUSER" -d "$PG_DB" >/dev/null || die "postgres не отвечает"

NEW_PASSWORD="$(rand_chars 43 'A-Za-z0-9_-')"

log "Меняю пароль роли $PG_SUPERUSER..."
# Пароль и имя роли — через stdin (\set), не аргументами: не видны в `ps`. :"u" — идентификатор,
# :'p' — строковый литерал, psql сам экранирует.
printf "\\\\set u '%s'\n\\\\set p '%s'\nALTER ROLE :\"u\" WITH PASSWORD :'p';\n" "$PG_SUPERUSER" "$NEW_PASSWORD" \
    | dc exec -T postgres psql -v ON_ERROR_STOP=1 -q -U "$PG_SUPERUSER" -d "$PG_DB" >/dev/null

if [ "$WRITE_ENV" -eq 1 ]; then
    tmp="$(mktemp "${ENV_FILE}.XXXXXX")"
    sed "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=$NEW_PASSWORD|" "$ENV_FILE" > "$tmp"
    chmod --reference="$ENV_FILE" "$tmp" 2>/dev/null || chmod 600 "$tmp"
    mv "$tmp" "$ENV_FILE"
    log "POSTGRES_PASSWORD обновлён в $ENV_FILE. Чтобы backup подхватил его: docker compose up -d backup"
fi

log "Готово. Пароль изменён в БД. Теперь ОБЯЗАТЕЛЬНО (см. шапку скрипта): обновить PROD_ENV и задеплоить."
echo "POSTGRES_PASSWORD=$NEW_PASSWORD"
