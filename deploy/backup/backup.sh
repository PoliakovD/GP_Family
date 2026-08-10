#!/usr/bin/env bash
# Ночной бэкап Postgres + MinIO (часть H деплой-плана). Запускается cron'ом контейнера backup
# (см. Dockerfile, 03:30 ежедневно) — но можно и вручную: `docker compose run --rm backup /app/backup.sh`.
#
# Ротация: 7 ежедневных дампов Postgres + 4 еженедельных (по воскресеньям, `date +%u` == 7).
# MinIO — не версии, а всегда актуальное зеркало бакета (`mc mirror --overwrite --remove`):
# файлы вложений неизменяемы после загрузки (см. StorageKeyFactory — опаковые ключи, перезапись
# по тому же ключу не предусмотрена бизнес-логикой), полная история версий не нужна — важна
# только точка восстановления "как сейчас".
set -euo pipefail
shopt -s nullglob

: "${POSTGRES_HOST:?}"
: "${POSTGRES_DB:?}"
: "${POSTGRES_USER:?}"
: "${POSTGRES_PASSWORD:?}"
: "${MINIO_ENDPOINT:?}"
: "${MINIO_ROOT_USER:?}"
: "${MINIO_ROOT_PASSWORD:?}"
: "${MINIO_BUCKET:?}"

DB_DAILY_DIR=/backups/db/daily
DB_WEEKLY_DIR=/backups/db/weekly
MINIO_MIRROR_DIR=/backups/minio
KEEP_DAILY=7
KEEP_WEEKLY=4

mkdir -p "$DB_DAILY_DIR" "$DB_WEEKLY_DIR" "$MINIO_MIRROR_DIR"

timestamp=$(date +%Y%m%d-%H%M)
dump_file="$DB_DAILY_DIR/familyhub-$timestamp.dump"

echo "[$(date -Iseconds)] pg_dump -> $dump_file"
PGPASSWORD="$POSTGRES_PASSWORD" pg_dump \
    -h "$POSTGRES_HOST" -U "$POSTGRES_USER" -Fc "$POSTGRES_DB" \
    -f "$dump_file"
chmod 600 "$dump_file"

echo "[$(date -Iseconds)] Проверка целостности дампа (pg_restore --list)..."
if ! pg_restore --list "$dump_file" >/dev/null; then
    echo "[$(date -Iseconds)] ОШИБКА: дамп $dump_file повреждён или пуст." >&2
    exit 1
fi

# Воскресенье (ISO — 7-й день недели) — копия дампа в еженедельный архив.
if [ "$(date +%u)" -eq 7 ]; then
    weekly_file="$DB_WEEKLY_DIR/familyhub-$timestamp.dump"
    cp "$dump_file" "$weekly_file"
    chmod 600 "$weekly_file"
    echo "[$(date -Iseconds)] Воскресная копия -> $weekly_file"
fi

echo "[$(date -Iseconds)] Ротация: оставляю последние $KEEP_DAILY ежедневных / $KEEP_WEEKLY еженедельных..."
# nullglob (см. верх файла) — если файлов ещё меньше keep (первые дни после разворачивания),
# glob разворачивается в пустой массив, а не в литеральную строку "familyhub-*.dump"; без этого
# `ls` на несуществующий литеральный путь падал бы с ненулевым кодом, а set -o pipefail
# протащил бы этот код через весь конвейер — проверено: именно так сломался первый вариант скрипта.
rotate() {
    local dir="$1" keep="$2"
    local files=("$dir"/familyhub-*.dump)
    if [ "${#files[@]}" -gt "$keep" ]; then
        # ls -1t — новые первыми; tail оставляет всё, что старше keep-й позиции, на удаление.
        ls -1t "${files[@]}" | tail -n +"$(( keep + 1 ))" | xargs -r rm -f
    fi
}
rotate "$DB_DAILY_DIR" "$KEEP_DAILY"
rotate "$DB_WEEKLY_DIR" "$KEEP_WEEKLY"

echo "[$(date -Iseconds)] mc mirror MinIO ($MINIO_BUCKET) -> $MINIO_MIRROR_DIR..."
mc alias set backup-target "http://$MINIO_ENDPOINT" "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null
mc mirror --overwrite --remove "backup-target/$MINIO_BUCKET" "$MINIO_MIRROR_DIR"

echo "[$(date -Iseconds)] Готово."
