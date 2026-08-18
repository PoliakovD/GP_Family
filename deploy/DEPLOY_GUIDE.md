# Чек-лист деплоя FamilyHub на VPS

Пошаговый чек-лист поверх двух существующих документов — держите их под рукой:
- **[`README.md`](README.md)** — подробности каждого шага (команды, объяснения "как").
- **[`DECISIONS.md`](DECISIONS.md)** — почему выбрано именно так, и таблица несостыковок.

Этот файл ничего не объясняет заново — он даёт линейный порядок действий и точные значения
(генерация секретов, готовый шаблон `.env`), которые в README разбросаны по секциям.

## Предполётный чек-лист

- [ ] Чистый VPS, Ubuntu 24.04, ≥6 CPU / ≥12 GB RAM / ≥120 GB SSD, root-доступ по SSH.
- [ ] Домен, который можно направить на VPS (доступ к DNS-зоне).
- [ ] Права `Settings → Secrets and variables → Actions` в GitHub-репозитории.
- [ ] (Опционально) ноутбук с LM Studio для реального OCR/суммаризации медикаментов — если
      его не будет, конвейер работает в деградированном режиме (`/health/llm` → Degraded,
      не Unhealthy), можно пропустить шаг 3.

## Шаг 1 — провижининг VPS

```bash
ssh-keygen -t ed25519 -f familyhub_deploy_key -C "github-actions-deploy"
scp bootstrap.sh root@<IP сервера>:/root/
ssh root@<IP сервера> 'bash /root/bootstrap.sh "'"$(cat familyhub_deploy_key.pub)"'"'
```

Скрипт идемпотентен. По завершении печатает публичный IP, готовый конфиг WireGuard для ноутбука
и список GitHub Secrets. **До закрытия root-сессии** проверьте вход под `deploy` в другом
терминале — `PasswordAuthentication` уже выключен:

```bash
ssh -i familyhub_deploy_key deploy@<IP сервера>
```

Детали: [`README.md` §2](README.md#2-провижининг-vps).

## Шаг 2 — DNS

| Запись | Значение |
|---|---|
| `<домен>`, `www.<домен>` | A → публичный IP VPS |
| `seq.<домен>` | A → `10.8.0.1` |
| `s3.<домен>` | A → `10.8.0.1` |
| `admin.<домен>` | A → `10.8.0.1` |

Приватный адрес в публичной зоне — нормально: без WireGuard `10.8.0.1` никуда не резолвится
достижимо. Детали: [`README.md` §3](README.md#3-dns).

## Шаг 3 — WireGuard на ноутбуке (только если нужен реальный LM Studio)

1. Импортируйте `familyhub-laptop.conf`, напечатанный `bootstrap.sh`.
2. Включите туннель, `ping 10.8.0.1` должен отвечать.
3. LM Studio → Developer → Local Server → слушать `0.0.0.0:1234`, не `127.0.0.1`.
4. Разрешите входящие на порт 1234 в брандмауэре для WireGuard-интерфейса.
5. Проверка с сервера: `curl http://10.8.0.2:1234/v1/models`.

**Критично**: `AllowedIPs = 10.8.0.0/24, 172.16.0.0/12` в конфиге ноутбука — второй диапазон
покрывает docker-мосты на VPS, без него ответ от LM Studio до контейнера `api` не дойдёт.
Детали и корневой CA Caddy для админ-доменов: [`README.md` §4](README.md#4-wireguard-на-ноутбуке-lm-studio).

## Шаг 4 — GitHub Secrets

`Settings → Secrets and variables → Actions → New repository secret`. Рекомендуется завести
GitHub Environment `production` с этими же секретами (`deploy.yml` уже ссылается на
`environment: production`) — так можно включить обязательное ревью перед запуском.

### SSH-доступ

| Секрет | Значение | Как получить |
|---|---|---|
| `SSH_HOST` | IP или домен VPS | из вывода `bootstrap.sh` |
| `SSH_USER` | `deploy` | создан `bootstrap.sh` |
| `SSH_PRIVATE_KEY` | содержимое `familyhub_deploy_key` (**приватный** ключ, не `.pub`) | `cat familyhub_deploy_key` |
| `SSH_KEY_PASSPHRASE` | passphrase ключа (если задавали при `ssh-keygen`; иначе не заводите секрет) | та, что вводили в `ssh-keygen` |
| `SSH_KNOWN_HOSTS` | отпечаток хоста | `ssh-keyscan <IP сервера>` со своей машины |

### `PROD_ENV` — содержимое всего `.env` для прода

Каждая из этих переменных **обязательна** — приложение откажется стартовать без неё
(fail-fast-проверки в `Program.cs`) либо (для `DevTools__Admin*`) откроет Hangfire/Swagger
без пароля:

| Переменная | Обязательна, когда | Генерация |
|---|---|---|
| `ENCRYPTION_MASTER_KEY` | всегда | `openssl rand -base64 32` |
| `Jwt__SigningKey` | всегда (+ должен быть валидным base64 — плейсхолдер `CHANGE_ME` не пройдёт) | `openssl rand -base64 32` |
| `Attachments__DownloadSigningKey` | всегда, ≥32 символов | `openssl rand -base64 32` |
| `POSTGRES_PASSWORD` | всегда | `openssl rand -base64 24` |
| `MINIO_ROOT_PASSWORD` | всегда | `openssl rand -base64 24` |
| `SEQ_ADMIN_PASSWORD_HASH` | всегда (на VPS `SEQ_FIRSTRUN_NOAUTHENTICATION=false`) | `docker run --rm datalust/seq config hash <пароль>` |
| `DevTools__AdminUser` / `DevTools__AdminPassword` | т.к. на VPS `DevTools__AdminUiEnabled=true` | пароль: `openssl rand -base64 24` |
| `Telegram__BotToken`, `Telegram__WebhookSecret` | если нужен бот | @BotFather / любая случайная строка |
| `Enrichment__ApiKey` | если `Enrichment__Provider` ≠ `Null` | у выбранного провайдера (Brave/Yandex) |
| `Enrichment__FolderId` | только если `Enrichment__Provider=Yandex` | Yandex Cloud console |
| `Email__PublicSiteUrl` | если заданы `Email__Providers__*` | ваш домен, `https://` |

**Не переиспользуйте значения из локальных `.env`/`prod.env`/`yandex.env`** — заводите новые.
Если в `prod.env` у вас уже лежит реальный SMTP-пароль (например Yandex Postbox) — его тоже
нужно ротировать, а не просто не копировать: он мог утечь через локальную машину/чат, где
обсуждался.

Готовый шаблон — возьмите [`.env.example`](../.env.example) целиком и примените поверх него:

```bash
# 1. Секреты — см. таблицу выше, каждый CHANGE_ME на реальное значение.

# 2. Docker-сетевые адреса вместо localhost (внутри compose):
Minio__Endpoint=minio:9000
Messaging__Kafka__Enabled=true
Messaging__Kafka__BootstrapServers=kafka:9092
Serilog__WriteTo__1__Args__serverUrl=http://seq:80

# 3. Дев-по-возможностям, прод-по-защите (см. DevToolsOptions):
DevTools__AdminUiEnabled=true
DevTools__DevAuthEnabled=false
DevTools__DevEndpointsEnabled=false
Serilog__MinimumLevel__Default=Debug

# 4. Адрес ноутбука в WireGuard-сети, НЕ host.docker.internal (это дев-адрес):
LmStudio__BaseUrl=http://10.8.0.2:1234
# Если LM Studio не используется на проде — оставьте как есть, деградация уже в коде.

# 5. Публичные URL:
Telegram__WebhookUrl=https://<ваш-домен>/bot/webhook
Email__PublicSiteUrl=https://<ваш-домен>

# 6. VPS-секция (низ .env.example):
PUBLIC_DOMAIN=<ваш-домен>
ACME_EMAIL=admin@<ваш-домен>
```

**Не добавляйте `IMAGE`/`IMAGE_TAG`** в `PROD_ENV` — их дописывает сам `deploy.yml` перед
выгрузкой на сервер (тег образа решает пайплайн запуска, а не секрет).

Полный список переменных и их роль — [`.env.example`](../.env.example) (единственный
закоммиченный env-файл, подробно прокомментирован построчно).

## Шаг 5 — первый деплой

`Actions → Deploy → Run workflow` (ветка/тег в поле `ref`, обычно `master`; `run_tests: true`).

Что делает workflow по шагам:
1. `test` — юнит-тесты (если `run_tests=true`).
2. `build-push` — собирает `src/FamilyHub.Api/Dockerfile`, пушит в
   `ghcr.io/<owner>/gp_family-api` двумя тегами (`<git-sha>` и `latest`).
3. `deploy` — по SSH кладёт на VPS `.env` (из `PROD_ENV` + тег образа), `docker-compose.yml`,
   `Caddyfile`, `backup/`; `docker compose pull && up -d --remove-orphans`; ждёт
   `/health/ready` изнутри контейнера `api` (до ~90 сек); чистит старые образы.

Первый запуск сам применит EF Core миграции (retry с backoff в `Program.cs`) и создаст Kafka-топики.

Триггер только ручной (`workflow_dispatch`) — ничто не уезжает на VPS автоматически по push.

## Шаг 6 — проверка после деплоя

```bash
curl -i https://<домен>/                                    # SPA, 200
curl -i https://<домен>/hangfire                             # 404 — закрыт на Caddy
curl -i -H 'X-Dev-TelegramId: 1' https://<домен>/api/families # 401, не 200
```

Через WireGuard (после установки корневого CA Caddy, см. README §4):
- `https://seq.<домен>:8443` — логи Debug-уровня.
- `https://admin.<домен>:8443/hangfire` — спрашивает BasicAuth (`DevTools__AdminUser/Password`).
- `https://s3.<домен>:8443` — консоль MinIO.

Без WireGuard все три недостижимы (порт `8443` привязан к `10.8.0.1`, см.
[`README.md` §8](README.md#8-проверка-после-деплоя) — почему).

## Шаг 7 — откат

Без нового запуска workflow, прямо на сервере:

```bash
ssh deploy@<IP>
cd /opt/familyhub
sed -i 's/^IMAGE_TAG=.*/IMAGE_TAG=<предыдущий git-sha>/' .env
docker compose up -d api
```

Предыдущие теги — в GHCR (Packages на GitHub) или в истории успешных запусков `Deploy`.

## Бэкапы

### `deploy/backup/` vs `deploy/backups/` — не путать

| | `deploy/backup/` (без «s») | `deploy/backups/` (с «s») |
|---|---|---|
| Что это | Код: `backup.sh` + `Dockerfile` ночного бэкапа | Данные: сами дампы Postgres + зеркало MinIO |
| Где живёт | В репозитории, `deploy/backup/` | Только на VPS, `/opt/familyhub/backups/` (создаётся `bootstrap.sh`, mode 700) |
| В git? | **Да, должно быть** | **Нет, и не должно быть** — это runtime-данные, не код |

Ранее `deploy/backup/` (код) был по ошибке git-ignored — правило `Backup*/` в `.gitignore`
(регистронезависимо на Windows) ловило и его. Из-за этого `deploy.yml` (шаг `scp -r deploy/backup`)
не имел, что копировать на свежий чекаут, и деплой ломался при сборке сервиса `backup`
(`build: context: ./backup`). Исправлено — добавлено явное исключение в `.gitignore`, файлы
закоммичены. Проверить, что не откатится обратно:

```bash
git ls-files deploy/backup/
# ожидается: deploy/backup/Dockerfile
#             deploy/backup/backup.sh
```

`deploy/backups/` при этом никогда не было и не будет в git — и это правильно: это боевые
дампы БД, не код.

### Как это работает

Автоматически в 03:30 (после Hangfire `audit-retention` в 03:00). Ротация: 7 ежедневных +
4 еженедельных (воскресных) дампа Postgres (`pg_dump -Fc`, с проверкой целостности через
`pg_restore --list` перед сохранением); MinIO — актуальное зеркало бакета
(`mc mirror --overwrite --remove`), не версии.

Ручной запуск и проверка целостности:

```bash
ssh deploy@<IP>
cd /opt/familyhub
docker compose run --rm backup /app/backup.sh
docker compose run --rm backup pg_restore --list /backups/db/daily/<последний файл>.dump
```
(`pg_restore --list` нужно выполнять в сервисе `backup` — туда смонтирован `/backups`,
не в `postgres`.)

### Восстановление из бэкапа

Порядок для восстановления Postgres из дампа (например, после потери данных или отката на
более раннюю точку):

```bash
ssh deploy@<IP>
cd /opt/familyhub

# 1. Остановить api, чтобы не писал в базу во время restore.
docker compose stop api

# 2. Восстановить дамп: --clean --if-exists — безопасно поверх существующей (пустой или нет) базы.
docker compose run --rm backup bash -c \
  'PGPASSWORD="$POSTGRES_PASSWORD" pg_restore -h postgres -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
   --clean --if-exists /backups/db/daily/<файл>.dump'
# (для еженедельного бэкапа — путь /backups/db/weekly/<файл>.dump)

# 3. Восстановить вложения MinIO из зеркала — обратное направление того же mc mirror:
docker compose run --rm backup bash -c \
  'mc alias set restore-target "http://minio:9000" "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" && \
   mc mirror --overwrite /backups/minio "restore-target/$MINIO_BUCKET"'

# 4. Поднять api обратно.
docker compose up -d api
curl -i https://<домен>/health/ready
```

### Известное расхождение с политикой

[`docs/security/backup-and-retention-policy.md`](../docs/security/backup-and-retention-policy.md)
описывает требование хранить бэкапы отдельно от прод-сервера, 14 дней. Фактическая реализация —
7 ежедневных + 4 еженедельных дампа **на том же VPS**, без офлайн-копии. Это не забытая
доработка, а осознанно принятый риск — см. [`README.md` §10, п.2](README.md#10-известные-риски-и-осознанно-принятые-ограничения):
полная потеря VPS = полная потеря и данных, и их бэкапов. Перенос копий за пределы VPS в объём
этой задачи не входил.

## Troubleshooting

| Симптом | Причина / что проверить |
|---|---|
| `scp deploy/backup` в логе `deploy.yml` ничего не находит | `.gitignore` снова исключает `deploy/backup/` — проверьте `git ls-files deploy/backup/` (см. выше) |
| `/health/ready` не отвечает за ~90 сек, деплой падает на этом шаге | `docker compose logs --tail=200 api` (workflow сам печатает это при failure). Частые причины: `Jwt__SigningKey` не валидный base64, `ENCRYPTION_MASTER_KEY` не задан или равен dev-ключу из `.env.example`, Postgres/MinIO/Kafka ещё не прошли healthcheck |
| `docker login ghcr.io` падает на VPS | `GITHUB_TOKEN` в workflow должен иметь `packages: write` (уже выставлено в `deploy.yml`); проверьте, что пакет `gp_family-api` в GHCR доступен для чтения из этого аккаунта |
| Деплой прошёл, но `/hangfire`/`/swagger` отвечают 401 без пароля не для админ-домена, а на публичном `<домен>` | Caddy должен блокировать `/hangfire*`/`/swagger*`/`/dev/*` на публичном сайте — проверьте, что `deploy/Caddyfile` реально скопировался (тот же класс проблемы, что и с `deploy/backup/`, если правило `.gitignore` когда-нибудь тронут) |
| WireGuard поднят, но `seq.<домен>:8443` недоступен | Порт `8443` слушается только на `10.8.0.1` (см. Шаг 6) — убедитесь, что запрос реально идёт через туннель, а не напрямую в интернет |
| Шаг «Снять passphrase с ключа» падает на `ssh-keygen -p` | `SSH_KEY_PASSPHRASE` не задан или не совпадает с реальным паролем ключа — заведите/обновите секрет (см. Шаг 4) |
