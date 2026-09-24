# Деплой FamilyHub на VPS

Дев-контур по возможностям (Debug-логи в Seq, рабочий Hangfire-дашборд), прод-контур по защите
(TLS, firewall, BasicAuth на служебных UI, WireGuard-периметр для админок). Этот файл — инструкция
"как выполнить"; почему каждое решение принято именно так (и где по ходу реализации всплыли
несостыковки с первоначальным планом) — **[`DECISIONS.md`](DECISIONS.md)**.

## Состав

| Файл | Роль |
|---|---|
| **[`DEPLOY_GUIDE.md`](DEPLOY_GUIDE.md)** | Пошаговый чек-лист поверх этого файла и `DECISIONS.md` — линейный порядок действий с готовым шаблоном `.env` |
| `DECISIONS.md` | Рационале инфраструктурных решений + таблица несостыковок ("почему так", не "как выполнить") |
| `bootstrap.sh` | Разовый провижининг чистого VPS (Ubuntu 24.04): Docker, firewall, fail2ban, WireGuard, пользователь `deploy` |
| `docker-compose.prod.yml` | Прод-стек: api (образ из GHCR), postgres, minio, kafka, seq, caddy, backup |
| `Caddyfile` | Реверс-прокси: публичный сайт + WG-only админ-сайты (Seq/MinIO Console/Hangfire/Swagger) |
| `backup/` | Ночной pg_dump + зеркало MinIO с ротацией |
| `minio/Dockerfile` | MinIO + mc, собранные из исходников (ADR-0012); публикует workflow `../.github/workflows/minio-image.yml` в наш GHCR |
| `scripts/` | Операторские скрипты (ADR-0011): первичная настройка учёток приложения, смена пароля суперпользователя Postgres и root MinIO |
| `../.github/workflows/deploy.yml` | GitHub Actions: сборка образа → GHCR → SSH-деплой (только вручную) |
| `../.github/workflows/ci.yml`, `integration.yml` | Гейты перед деплоем (build+unit на каждый push, integration на master) |
| `../src/FamilyHub.Api/Dockerfile` | Продовый образ (Angular + .NET в одном контейнере) |

## 1. Требования

- VPS: 6 CPU / 12 GB RAM / 120 GB SSD, чистая **Ubuntu 24.04**.
- Домен (в тексте — `gp-family.ru`, замените на свой) с доступом к DNS-зоне.
- Ноутбук с LM Studio (`http://localhost:1234`, OpenAI-совместимый сервер) — для OCR/суммаризации
  медикаментов. Его недоступность **не блокирует контур** — деградация уже реализована в коде
  (`LmStudioJsonClient`, `/health/llm` возвращает Degraded, не Unhealthy).
- Права на репозиторий на GitHub для настройки Secrets и запуска Actions.

## 2. Провижининг VPS

Сгенерируйте отдельную SSH-пару для деплоя (не переиспользуйте личный ключ):

```bash
ssh-keygen -t ed25519 -f familyhub_deploy_key -C "github-actions-deploy"
```

Скопируйте и запустите `bootstrap.sh` от root на свежем сервере, передав **публичный** ключ:

```bash
scp bootstrap.sh root@<IP сервера>:/root/
ssh root@<IP сервера> 'bash /root/bootstrap.sh "'"$(cat familyhub_deploy_key.pub)"'"'
```

Скрипт идемпотентен — повторный запуск безопасен. По завершении печатает:
- публичный IP сервера;
- готовый конфиг WireGuard для ноутбука;
- список GitHub Secrets, которые нужно завести.

**Проверьте вход под `deploy` в отдельном терминале ДО того, как закроете root-сессию** —
`PasswordAuthentication` уже выключен:

```bash
ssh -i familyhub_deploy_key deploy@<IP сервера>
```

## 3. DNS

| Запись | Значение |
|---|---|
| `gp-family.ru`, `www.gp-family.ru` | A → публичный IP VPS |
| `seq.gp-family.ru` | A → `10.8.0.1` |
| `s3.gp-family.ru` | A → `10.8.0.1` |
| `admin.gp-family.ru` | A → `10.8.0.1` |

Приватный адрес в публичной DNS-зоне — это нормально: без подключения к WireGuard `10.8.0.1` не
резолвится ни во что достижимое, запрос просто не уйдёт никуда. Это удобнее, чем править
`hosts` на каждом устройстве, с которого нужен доступ к админкам.

## 4. WireGuard на ноутбуке (LM Studio)

1. Импортируйте `familyhub-laptop.conf`, напечатанный `bootstrap.sh`, в клиент WireGuard
   (официальное приложение для Windows/macOS/Linux).
2. Включите туннель — `ping 10.8.0.1` с ноутбука должен отвечать.
3. В LM Studio: Developer → Local Server → сервер должен слушать `0.0.0.0:1234`, не `127.0.0.1`
   (иначе он не увидит запросы, пришедшие через туннель).
4. В брандмауэре Windows разрешите входящие подключения на порт 1234 для профиля, под которым
   поднят интерфейс WireGuard.
5. Проверка с сервера: `curl http://10.8.0.2:1234/v1/models` (не только `ping` — TCP до
   конкретного порта проверяет и правило файрвола ноутбука, а не только сам туннель).

**Важно про `AllowedIPs`**: в конфиге ноутбука должно быть `10.8.0.0/24, 172.16.0.0/12` — второй
диапазон покрывает docker-мосты на VPS. Без него запрос от контейнера `api` до `10.8.0.2:1234`
дойдёт, а ответ уйдёт мимо туннеля и запрос молча провисит до таймаута.

### Корневой сертификат Caddy для админ-доменов

Админ-сайты (`seq.*`, `s3.*`, `admin.*`) используют `tls internal` — Let's Encrypt не выдаст
сертификат на имя, резолвящееся в приватный `10.8.0.1`. Чтобы браузер не ругался:

1. После первого `docker compose up -d` заберите корневой сертификат с сервера:
   ```bash
   scp deploy@<IP>:/opt/familyhub/caddy-data/caddy/pki/authorities/local/root.crt ./familyhub-ca.crt
   ```
2. Установите его как доверенный корневой центр сертификации на устройствах, с которых заходите
   в админки (Windows: двойной клик → «Установить сертификат» → «Доверенные корневые центры
   сертификации»).

Без этого шага сайты всё равно работают — просто с предупреждением браузера о самоподписанном
сертификате.

## 5. GitHub Secrets

Settings → Secrets and variables → Actions → New repository secret:

| Секрет | Значение |
|---|---|
| `SSH_HOST` | IP или домен сервера |
| `SSH_USER` | `deploy` |
| `SSH_PORT` | SSH-порт, если он не `22` (см. `sshd_config` на VPS); секрет не заведён -> `deploy.yml` использует `22` |
| `SSH_PRIVATE_KEY` | содержимое `familyhub_deploy_key` (приватный ключ, **не** `.pub`) |
| `SSH_KEY_PASSPHRASE` | passphrase ключа, если при `ssh-keygen` она была задана; иначе оставьте секрет пустым (или не создавайте) |
| `SSH_KNOWN_HOSTS` | вывод `ssh-keyscan -p <SSH_PORT> <IP сервера>` со своей машины (укажите `-p`, если порт не `22`, иначе `ssh -p ...` не найдёт хост в `known_hosts`) |
| `PROD_ENV` | весь файл `.env` для прода целиком (см. ниже) |

`webfactory/ssh-agent` (используется в `deploy.yml` для `ssh-add`) не умеет ключи с passphrase —
в CI некому ввести пароль интерактивно, `ssh-add` просто зависнет/упадёт. Поэтому `deploy.yml`
сначала снимает passphrase отдельным шагом (`ssh-keygen -p -P "$SSH_KEY_PASSPHRASE" -N ""`) и
только потом отдаёт уже беспарольный ключ в `ssh-agent`; сам ключ с паролем в `SSH_PRIVATE_KEY`
менять не нужно.

Рекомендуется завести GitHub Environment `production` (Settings → Environments) с этими же
секретами и, при желании, обязательным ревью перед запуском — `deploy.yml` уже ссылается на
`environment: production`.

### Формирование `PROD_ENV`

Возьмите `.env.example` за основу и:
1. Замените все `CHANGE_ME` реальными значениями. Секреты — **новые**, не переиспользуйте
   значения из локального `.env`/`prod.env`:
   - `ENCRYPTION_MASTER_KEY`, `Jwt__SigningKey`, `Attachments__DownloadSigningKey` —
     `openssl rand -base64 32` каждый;
   - `POSTGRES_PASSWORD`, `MINIO_ROOT_PASSWORD` — аналогично (это учётки для бэкапа и ручного
     администрирования; приложение под ними не работает);
   - `DB_APP_USER`, `DB_APP_PASSWORD`, `MINIO_APP_ACCESS_KEY`, `MINIO_APP_SECRET_KEY` — **не
     генерируйте руками**: их выпускает `scripts/bootstrap-app-credentials.sh` (см. ниже,
     «Учётки приложения»);
   - `SEQ_ADMIN_PASSWORD_HASH` — `docker run --rm datalust/seq config hash <пароль>`;
   - `DevTools__AdminUser`/`DevTools__AdminPassword` — учётка для Hangfire/Swagger.
2. Выставите docker-сетевые адреса (внутри compose, не localhost):
   ```
   Minio__Endpoint=minio:9000
   Messaging__Kafka__Enabled=true
   Messaging__Kafka__BootstrapServers=kafka:9092
   Serilog__WriteTo__1__Args__serverUrl=http://seq:80
   ```
3. Дев-по-возможностям, прод-по-защите (см. `DevToolsOptions`):
   ```
   DevTools__AdminUiEnabled=true
   DevTools__DevAuthEnabled=false
   DevTools__DevEndpointsEnabled=false
   Serilog__MinimumLevel__Default=Debug
   ```
4. `LmStudio__BaseUrl=http://10.8.0.2:1234` — адрес ноутбука в WireGuard-сети, не
   `host.docker.internal` (это дев-стековый адрес, на VPS не резолвится).
5. `Telegram__WebhookUrl=https://gp-family.ru/bot/webhook`, `Email__PublicSiteUrl=https://gp-family.ru`.
6. **Не добавляйте `IMAGE`/`IMAGE_TAG`** — их дописывает сам `deploy.yml` перед выгрузкой на
   сервер (тег решает пайплайн, не секрет).

## 6. Первый деплой

Actions → Deploy → Run workflow (ветка `master`, `run_tests: true`). Workflow:
1. Гоняет юнит-тесты (если `run_tests=true`).
2. Собирает `src/FamilyHub.Api/Dockerfile`, пушит в `ghcr.io/<owner>/gp_family-api` двумя тегами
   (`<git-sha>` и `latest`).
3. По SSH кладёт на сервер `.env` (из `PROD_ENV` + тег образа), `docker-compose.yml`, `Caddyfile`,
   `backup/`.
4. `docker compose pull && up -d --remove-orphans`, ждёт `/health/ready` изнутри контейнера
   `api` (до полутора минут), затем чистит старые образы.

Первый запуск также автоматически применит все EF Core миграции (см. `Program.cs` — retry с
экспоненциальной паузой) и создаст Kafka-топики (`EnsureTopicsExist`, ADR-0007).

## 7. Откат

Не требует нового запуска workflow — быстрее откатить прямо на сервере:

```bash
ssh deploy@<IP>
cd /opt/familyhub
sed -i 's/^IMAGE_TAG=.*/IMAGE_TAG=<предыдущий git-sha>/' .env
docker compose up -d api
```

Предыдущие теги видны в GHCR (Packages на GitHub) или в истории успешных запусков `Deploy`.

## 8. Проверка после деплоя

```bash
curl -i https://gp-family.ru/                      # SPA, 200
curl -i https://gp-family.ru/hangfire               # 404 — закрыт на Caddy
curl -i https://gp-family.ru/dev/email-preview/x    # 404/200-SPA-fallback, но НЕ рендер письма
curl -i -H 'X-Dev-TelegramId: 1' https://gp-family.ru/api/families   # 401, не 200
```

Через WireGuard (после установки корневого CA, см. §4):
- `https://seq.gp-family.ru:8443` — логи с Debug-уровнем;
- `https://admin.gp-family.ru:8443/hangfire` — спрашивает BasicAuth (`DevTools__AdminUser/Password`),
  затем показывает `reminder-scan`, `audit-retention`, сервер `enrichment-server`;
- `https://s3.gp-family.ru:8443` — консоль MinIO;
- `https://admin.gp-family.ru:4059` — админ-панель (ADR-0009): своя форма входа
  (`Admin__User/Password`), НЕ тот же логин, что у `:8443/hangfire` выше. Тот же хост-лейбл
  `admin.`, что и `:8443` — Caddy различает их по порту (см. deploy/Caddyfile), путаница в
  адресной строке возможна, если открывать оба одновременно.

Postgres — не HTTP, поэтому без Caddy: контейнер публикует `5432` напрямую на `10.8.0.1`
(host-IP-scoped, как и `8443`/`4059` выше). Подключение любым клиентом (DBeaver/pgAdmin/psql)
после поднятия WireGuard:

```
psql "host=10.8.0.1 port=5432 dbname=<POSTGRES_DB> user=<POSTGRES_USER> password=<POSTGRES_PASSWORD>"
```

Значения — из `.env` на VPS (или `PROD_ENV` в GitHub Secrets). TLS не настроен (трафик и так
идёт внутри WG-туннеля), поэтому в клиенте отключите/не включайте SSL-режим `require`.

Без WireGuard всё недостижимо (проверьте с мобильного интернета, не с WG-подключённого Wi-Fi).
С публичного домена `curl -i https://gp-family.ru/admin` и `.../api/admin/session` должны
отвечать 404 (см. `@blocked` в Caddyfile) — панель существует только на `:4059`.

**Про порт `:8443` в адресах админок**: WireGuard-интерфейс (`wg0`, `10.8.0.1`) существует только
на ХОСТЕ, а не внутри контейнера Caddy — Caddy физически не может забиндиться на этот адрес
изнутри контейнера. Вместо этого админ-сайты слушают отдельный контейнерный порт `8443`, который
Docker публикует конкретно на `10.8.0.1` (host-IP-scoped port mapping — работает на уровне docker,
не требует видимости `wg0` внутри контейнера). Публичный `443` при этом остаётся на `0.0.0.0` —
два разных контейнерных порта, конфликта биндинга нет.

### Ротация ключей приложения (ADR-0009)

Три независимых секрета (`Encryption:MasterKey`, `Jwt:SigningKey`, `Attachments:DownloadSigningKey`)
поддерживают связку активный+отставные ключи — смена активного ключа больше не событие потери
данных. Полные комментарии — в `.env.example` рядом с каждым секретом; общая процедура:

1. Сгенерировать новый ключ (`openssl rand -base64 32`, для Attachments можно любую строку).
2. В `prod.env`/секрете `PROD_ENV`: новое значение становится активным (`Jwt__SigningKey` и т.п.),
   СТАРОЕ переезжает в `*__Previous*` того же секрета (`Jwt__PreviousSigningKeys__0__Material` и т.д.).
3. Редеплой (§6/§7). Существующие данные/сессии/ссылки читаются без сбоя — приложение принимает
   оба ключа сразу после старта.
4. **Jwt/Attachments** — отставной ключ можно убрать из конфигурации уже через несколько минут
   (access-токен живёт `Jwt:AccessTokenLifetime`, по умолчанию 15 минут; ссылка на вложение —
   `Attachments:UrlTtl`, 5 минут) и передеплоить ещё раз.
5. **Encryption** — данные не перешифровываются сами. В админ-панели
   (`https://admin.gp-family.ru:4059`), «Безопасность → Ключи и ротация», нажать «Перешифровать» —
   фоновая джоба (`EncryptionRotationJob`, Hangfire-очередь `rotation`) обходит все
   `[Encrypted]`-поля и блобы вложений в MinIO. Прогресс виден на той же странице; прогон
   резюмируется сам при рестарте контейнера. Сколько данных осталось на каждом `keyId` — на
   странице «Безопасность → Статистика». Когда счётчики дойдут до нуля на старом `keyId` — убрать
   `Encryption__PreviousKeys__0__*` из конфигурации и передеплоить.

### Образ MinIO (ADR-0012)

Проект `minio/minio` больше не публикует образы, поэтому MinIO и `mc` собираются **из исходников** в
наш GHCR — `ghcr.io/<owner>/gp_family-minio:<тег релиза MinIO>`. На него ссылаются оба compose, образ
бэкапа и интеграционные тесты; версия закреплена (не `:latest`).

- **Собрать/обновить:** Actions → **MinIO image** → Run workflow (версии — в inputs; пусто = значения из
  `deploy/minio/Dockerfile`). Перед публикацией образ проходит смоук-тест. Workflow также запускается сам
  при изменении `deploy/minio/**`.
- **Видимость пакета.** Пакет из публичного репозитория опубликовался публичным (проверено анонимным
  `docker pull`), VPS и dev-машины тянут его без `docker login`. Если у вас он окажется приватным —
  GitHub → профиль → Packages → `gp_family-minio` → Package settings → Change visibility → Public.
  Секретов в образе нет.
- **Обновление версии:** правка `ARG MINIO_REF`/`MC_REF` → запуск workflow → тот же тег в
  `docker-compose.yml`, `deploy/docker-compose.prod.yml`, `deploy/backup/Dockerfile`,
  `tests/FamilyHub.IntegrationTests/TestImages.cs`.
- Образ должен быть в GHCR **до** деплоя, который на него ссылается (иначе `docker compose pull` падает).
- Upstream больше не выпускает исправления безопасности — см. риски в ADR-0012.

### Учётки приложения (ADR-0011)

Приложение ходит в Postgres под `familyhub_app_a` (владелец схем — роль `familyhub_owner`), в MinIO —
под service account пользователя с доступом только к бакету `familyhub`. Суперпользователь Postgres и
root MinIO остаются для бэкапа и ручной работы — контейнер `api` их пароли **не получает**.

**Первичная настройка на действующем проде — ОДИН раз, строго ДО деплоя версии с этими переменными.**
Прод-compose требует `DB_APP_*`/`MINIO_APP_*` и без них не запустит `api`; пока вы этого не сделали,
работающий стек не затронут (деплой упадёт на интерполяции до какого-либо пересоздания контейнеров).

1. С машины, где лежит репозиторий (ветка/коммит с этими скриптами), скопируйте скрипты на сервер:
   ```bash
   scp -r deploy/scripts deploy@<IP>:/opt/familyhub/
   ```
2. На сервере, в малонагруженное время (передача владения таблицами берёт короткие блокировки):
   ```bash
   ssh deploy@<IP>
   cd /opt/familyhub
   bash scripts/bootstrap-app-credentials.sh
   ```
   Скрипт создаёт роли и функции в Postgres, передаёт владение существующими объектами, заводит в MinIO
   пользователя/политику/service account и **один раз** печатает четыре строки:
   `DB_APP_USER`, `DB_APP_PASSWORD`, `MINIO_APP_ACCESS_KEY`, `MINIO_APP_SECRET_KEY`. Нигде, кроме
   вашего терминала, они не сохраняются. Работающий `api` продолжает работать под старыми кредами.
3. Добавьте эти четыре строки в секрет `PROD_ENV` (GitHub → Settings → Secrets).
4. Задеплойте (§6). `api` пересоздастся уже под новыми учётками.
5. Проверьте: `/health/ready` зелёный; сессии приложения видны под новой ролью —
   `docker compose exec postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "select usename, count(*) from pg_stat_activity group by 1"`
   (должна быть `familyhub_app_a`, а не суперпользователь).

Откат: задеплойте предыдущую версию репозитория (её compose снова использует `POSTGRES_*`/`MINIO_ROOT_*`) —
суперпользователь и root по-прежнему рабочие, обратная передача владения не нужна.

Повторный запуск того же скрипта без `--force` откажется (учётки уже заданы). С `--force` выпустит новый
пароль БД и новый ключ MinIO; старый ключ MinIO остаётся действующим, пока его не удалят
(`mc admin user svcacct rm`).

**Известное ограничение:** контейнер `caddy` по-прежнему получает весь `.env` (нужны лишь переменные
`Caddyfile`) — в том числе пароли суперпользователя/root. Сужение — отдельная задача (ADR-0011).

### Смена пароля суперпользователя Postgres и root MinIO

Приложение под ними не работает — смена **не требует простоя приложения**. Нужны они контейнеру
`backup` и вам (DBeaver/консоль MinIO).

```bash
# Postgres. Скрипт меняет пароль в БД и печатает POSTGRES_PASSWORD=...; --write-env заодно правит
# /opt/familyhub/.env, чтобы backup подхватил его до следующего деплоя.
bash scripts/rotate-postgres-superuser.sh --write-env
# → обновить POSTGRES_PASSWORD в PROD_ENV (иначе следующий деплой вернёт СТАРЫЙ пароль в .env и сломает
#   бэкап), задеплоить, обновить пароль в DBeaver.

# MinIO. Пароль root берётся сервером из env при старте, поэтому смена = новое значение в PROD_ENV + деплой.
bash scripts/rotate-minio-root.sh prepare    # печатает MINIO_ROOT_PASSWORD=... (ничего не меняет)
# → обновить MINIO_ROOT_PASSWORD в PROD_ENV, задеплоить (minio и backup пересоздадутся)
bash scripts/rotate-minio-root.sh verify     # root входит с новым паролем, ключ приложения работает
```

Ротация самих учёток приложения (пароль `familyhub_app_a/b`, ключ MinIO) — отдельная процедура
двухфазного переключения; из админ-панели она появится следующим этапом (ADR-0011).

## 9. Бэкапы

Автоматически в 03:30 (после Hangfire `audit-retention` в 03:00). Ручной запуск и проверка:

```bash
ssh deploy@<IP>
cd /opt/familyhub
docker compose run --rm backup /app/backup.sh
ls -la backups/db/daily/
docker compose exec postgres pg_restore --list /backups/db/daily/<последний файл>.dump
```

Бэкап работает под суперпользователем Postgres и root MinIO (не под учётками приложения) — так
он видит всё и не зависит от ротации ключей приложения. Дамп содержит `OWNER TO familyhub_owner`:
при восстановлении в **действующий** кластер (роли уже есть) ничего дополнительного не нужно; в
**новый** кластер сначала создайте роли — `psql -U <суперпользователь> -d <БД> -f
scripts/sql/bootstrap-roles.sql` (без пароля; пароль потом задаст `bootstrap-app-credentials.sh`) —
либо восстанавливайте с `pg_restore --no-owner --role=familyhub_owner`.

Ротация: 7 ежедневных + 4 еженедельных (воскресные) дампа Postgres; MinIO — актуальное зеркало
бакета (`mc mirror --overwrite --remove`), не версии. **Офлайн-копия вне сервера сознательно не
настроена** (см. риски ниже) — смерть VPS означает потерю и продовых данных, и их бэкапов.

## 10. Известные риски и осознанно принятые ограничения

1. **Один узел Kafka без реплик** — при потере диска VPS теряются недоставленные события.
   Outbox в Postgres (он тоже в бэкапе) остаётся источником правды до момента публикации.
2. **Бэкапы хранятся на том же VPS**, который бэкапят. Полная потеря сервера = полная потеря
   данных. Перенос копий за пределы VPS — сознательно не сделан в рамках этой задачи.
3. **CSP (`frame-ancestors 'none'`) может блокировать Mini App в веб-версии Telegram**
   (`web.telegram.org` открывает Mini App в `<iframe>`). Существует независимо от этого деплоя;
   если подтвердится — лечится заменой на `frame-ancestors https://web.telegram.org https://*.telegram.org`
   в `Program.cs`.
4. **Автомиграции на старте** (`Database.MigrateAsync()`, retry с backoff) требуют, чтобы
   пользователь БД приложения владел схемой — нормально для одного инстанса, но не подходит для
   будущего масштабирования на несколько реплик без внешней блокировки миграций.
5. **Секреты, требующие ротации перед первым деплоем** (см. §5.1) — дефолтный
   `ENCRYPTION_MASTER_KEY` и design-time-ключ навсегда остались в истории git; не копируйте их
   в `PROD_ENV`.
