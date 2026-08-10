# postgres:17-alpine — та же мажорная версия, что сам prod-Postgres (см. ../docker-compose.prod.yml):
# pg_dump строго не новее сервера, который дампит (обратная совместимость с более старым сервером
# не гарантируется). mc — просто скопированный бинарник из официального образа, без лишних слоёв.
FROM minio/mc:latest AS mc

FROM postgres:17-alpine

RUN apk add --no-cache bash gzip
COPY --from=mc /usr/bin/mc /usr/bin/mc

WORKDIR /app
COPY backup.sh ./backup.sh
RUN chmod +x ./backup.sh

# Ежедневно в 03:30 — после Hangfire-джобы audit-retention (03:00, см. Program.cs) и до утренней
# нагрузки. /proc/1/fd/1 — стдаут PID 1 (сам crond) — иначе вывод джобы уходил бы в никуда:
# у cron-джоб в busybox/vixie-cron нет tty и по умолчанию не наследуют stdout контейнера.
RUN echo "30 3 * * * /app/backup.sh >>/proc/1/fd/1 2>&1" > /etc/crontabs/root

ENTRYPOINT ["crond", "-f", "-l", "2"]
