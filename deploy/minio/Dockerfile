# MinIO Community Edition + mc, СОБРАННЫЕ ИЗ ИСХОДНИКОВ (ADR-0012).
#
# Зачем: проект minio/minio перестал поддерживаться и выпускать образы — образы на Docker Hub/Quay
# больше не обновляются и могут пропасть, а из исходников (Go) он по-прежнему собирается. Собираем
# сами и храним в своём GHCR (workflow .github/workflows/minio-image.yml), чтобы деплой не зависел
# от чужого реестра. Официальный Dockerfile MinIO для этого не подходит: он наследуется от
# minio/minio:latest (то есть от того самого недоступного образа), а Dockerfile.release скачивает
# готовые бинарники с dl.min.io.
#
# В образе — ДВА бинарника: minio (сервер) и mc (клиент). mc нужен не только «для удобства»: на нём
# healthcheck compose (`mc ready local`), скрипты deploy/scripts/*, а образ бэкапа
# (deploy/backup/Dockerfile) берёт бинарник mc отсюда.
#
# Версии закрепляются ТЕГАМИ upstream-репозиториев (аргументы ниже) — сборка воспроизводима, а
# обновление — осознанная смена аргумента, а не «latest». Точное соответствие исходникам (тег и
# коммит) записано в метках образа — это же и «предложение исходного кода» для AGPL-3.0 (см. ADR).
#
#   docker build -f deploy/minio/Dockerfile -t familyhub-minio deploy/minio

ARG GO_VERSION=1.24
FROM golang:${GO_VERSION}-alpine AS build

# git нужен и для клонирования, и самой сборке: buildscripts/gen-ldflags.go зашивает в бинарник
# коммит (`git rev-parse HEAD`), без него `minio --version` не показывал бы версию/коммит.
RUN apk add --no-cache git ca-certificates

# CGO_ENABLED=0 — статические бинарники (работают в любом базовом образе).
# GOTOOLCHAIN=auto — go.mod MinIO может требовать toolchain новее, чем в образе golang:1.24: пусть Go
# сам подтянет нужный (так и по умолчанию, зафиксировано явно на случай смены базового образа).
ENV CGO_ENABLED=0 GOTOOLCHAIN=auto

# ─── minio (сервер) ────────────────────────────────────────────────────────────────────────────
ARG MINIO_REF=RELEASE.2025-10-15T17-29-55Z
RUN git clone --depth 1 --branch "${MINIO_REF}" https://github.com/minio/minio.git /src/minio
WORKDIR /src/minio
# Версия для ldflags — в формате RFC3339 (`2025-10-15T17:29:55Z`), тег же использует дефисы вместо
# двоеточий во времени (`T17-29-55Z`) — тот же пересчёт делает upstream в Makefile (цель hotfix-vars).
# MINIO_RELEASE=RELEASE — чтобы `minio --version` показывал «RELEASE.<дата>», как официальные сборки,
# а не «DEVELOPMENT.<дата>».
RUN VERSION="$(echo "${MINIO_REF#RELEASE.}" | sed -E 's/T([0-9]+)-([0-9]+)-([0-9]+)Z/T\1:\2:\3Z/')" && \
    LDFLAGS="$(MINIO_RELEASE=RELEASE go run buildscripts/gen-ldflags.go "${VERSION}")" && \
    go build -tags kqueue -trimpath -ldflags "${LDFLAGS}" -o /out/minio . && \
    git rev-parse HEAD > /out/minio.commit

# ─── mc (клиент) ───────────────────────────────────────────────────────────────────────────────
ARG MC_REF=RELEASE.2025-08-13T08-35-41Z
RUN git clone --depth 1 --branch "${MC_REF}" https://github.com/minio/mc.git /src/mc
WORKDIR /src/mc
RUN VERSION="$(echo "${MC_REF#RELEASE.}" | sed -E 's/T([0-9]+)-([0-9]+)-([0-9]+)Z/T\1:\2:\3Z/')" && \
    LDFLAGS="$(MC_RELEASE=RELEASE go run buildscripts/gen-ldflags.go "${VERSION}")" && \
    go build -tags kqueue -trimpath -ldflags "${LDFLAGS}" -o /out/mc . && \
    git rev-parse HEAD > /out/mc.commit

# ─── итоговый образ ────────────────────────────────────────────────────────────────────────────
FROM alpine:3.21

ARG MINIO_REF=RELEASE.2025-10-15T17-29-55Z
ARG MC_REF=RELEASE.2025-08-13T08-35-41Z

# curl — как в официальном образе (проверки/отладка изнутри контейнера); ca-certificates — для
# исходящих TLS-соединений (KMS, webhook'и). Сервер работает от root, как и прежний образ: том
# minio-data на проде уже создан root'ом — смена пользователя потребовала бы миграции прав.
RUN apk add --no-cache ca-certificates curl && mkdir -p /data /licenses

COPY --from=build /out/minio /out/mc /usr/bin/
# entrypoint — из исходников MinIO без изменений: дописывает `minio` перед аргументами
# (`server /data ...` работает как раньше), поэтому compose/Testcontainers ничего не меняют.
COPY --from=build /src/minio/dockerscripts/docker-entrypoint.sh /usr/bin/docker-entrypoint.sh
# Лицензии — рядом с бинарниками (AGPL-3.0 требует сопровождать распространяемый код текстом лицензии).
COPY --from=build /src/minio/LICENSE /licenses/minio-LICENSE
COPY --from=build /src/minio/NOTICE /licenses/minio-NOTICE
COPY --from=build /src/minio/CREDITS /licenses/minio-CREDITS
COPY --from=build /src/mc/LICENSE /licenses/mc-LICENSE
COPY --from=build /src/mc/NOTICE /licenses/mc-NOTICE
COPY --from=build /out/minio.commit /licenses/minio.commit
COPY --from=build /out/mc.commit /licenses/mc.commit

# Те же переменные, что в официальном образе: поддержка Docker secrets через *_FILE и каталог
# конфигурации mc в /tmp — у скриптов и healthcheck (`mc ready local`) он рассчитан именно на это.
ENV MINIO_ACCESS_KEY_FILE=access_key \
    MINIO_SECRET_KEY_FILE=secret_key \
    MINIO_ROOT_USER_FILE=access_key \
    MINIO_ROOT_PASSWORD_FILE=secret_key \
    MINIO_KMS_SECRET_KEY_FILE=kms_master_key \
    MINIO_CONFIG_ENV_FILE=config.env \
    MC_CONFIG_DIR=/tmp/.mc

# Метки: source указывает на НАШ репозиторий (workflow переопределяет его значением репозитория,
# в котором запущен — так GHCR связывает пакет с репозиторием и наследует его права); откуда взяты
# исходники MinIO/mc — отдельными метками.
LABEL org.opencontainers.image.title="MinIO (self-built)" \
      org.opencontainers.image.description="MinIO Community Edition and mc built from upstream source tags" \
      org.opencontainers.image.licenses="AGPL-3.0-or-later" \
      io.minio.source="https://github.com/minio/minio" \
      io.minio.source.ref="${MINIO_REF}" \
      io.minio.mc.source="https://github.com/minio/mc" \
      io.minio.mc.source.ref="${MC_REF}"

EXPOSE 9000
VOLUME ["/data"]

ENTRYPOINT ["/usr/bin/docker-entrypoint.sh"]
CMD ["minio"]
