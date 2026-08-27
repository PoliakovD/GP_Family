# Аудит гонок/N+1/несостыковок (2026-08-27) — что исправлено, что осталось

Сквозной аудит бэкенда по запросу пользователя: три параллельных ресёрч-агента разобрали API-
эндпоинты, EF Core/доступ к БД и конкурентность/фон отдельно, находки перепроверены вручную
чтением кода, оформлены в отчёт-артефакт (25 находок с severity), затем часть исправлена в этой
же сессии. В отличие от `docs/security/module-review-2026-08-02/` (тот аудит — про security-бреши
вручную по модулям), этот заход — про корректность/гонки/производительность, найденные заодно
поиском N+1 и race condition.

## Методология проверки (важно для доверия к статусам ниже)

Каждый фикс из таблицы прогонялся через:
- `dotnet build` (0 ошибок) на затронутом проекте;
- полный `FamilyHub.UnitTests` (482 теста, SQLite in-memory);
- полный `FamilyHub.IntegrationTests` (182 теста, реальный PostgreSQL через Testcontainers/Docker —
  это единственный способ реально проверить raw-SQL/`FOR UPDATE`/провайдер-специфичные вещи, SQLite
  их либо не поддерживает синтаксически, либо покрывает не по-настоящему).

**Про попытку кэша в KbLookupService (важный урок, см. таблицу, находка H1):** первая версия фикса
N+1 добавила `IMemoryCache` прямо в `KbLookupService.LookupAsync` — компилировалось, юнит-тесты
(SQLite) были зелёными, но 3 интеграционных теста (`EnrichmentPipelineTests`,
`EnrichmentNameCorrectionTests`) стали падать по таймауту. Причина: тот же `LookupAsync` дёргает
`MedicationKbStatusService.BuildStatusAsync`, который фронт **поллит каждые ~300мс**, ожидая
увидеть свежее появление записи после фонового обогащения — первый же промах "залипал" в кэше на
10 минут, статус никогда не доходил до Ready. Кэш убрали, вместо него — batch-запрос точного
совпадения без хранения между вызовами (см. находку H1 ниже). Вывод для будущих фиксов в этом
файле: **любой кэш поверх `KbLookupService`/аналогичных read-your-own-write путей обязан либо
инвалидироваться на запись, либо не добавляться вовсе** — интеграционные тесты на реальном
Postgres это ловят, юнит-тесты на SQLite — нет, полагаться только на них для таких фиксов нельзя.

## Таблица находок

| # | Находка | Severity | Статус | Как исправлено (кратко) |
|---|---|---|---|---|
| C1 | Гонка на `FamilyInvite.UsedCount` — два одновременных погашения одного инвайта могли оба пройти проверку лимита | Critical | ✅ Исправлено | `InviteService.RedeemInviteAsync` — check-then-act заменён на `db.FamilyInvites.Where(...).ExecuteUpdateAsync(UsedCount+1, WHERE UsedCount < MaxUses)`; `affected == 0` ⇒ `Exhausted`. Атомарно на уровне БД, портативно (SQLite и Postgres) |
| C2 | `FileAttachment.ExtractedAt` проставлялся ДО сохранения результата распознавания — крах между этими точками терял файл навсегда, без пути на повтор | Critical | ✅ Исправлено | `MedicalDocumentExtractionProcessor` — `ExecuteUpdateAsync` вынесен из цикла по файлам в финальную транзакцию (та же, что сохраняет показатели/summary), через новый `MarkAttachmentsExtractedAsync` |
| C3 | После исчерпания ретраев `[AutomaticRetry]` задача навсегда виснет в `Status=Running` — частичный уникальный индекс блокирует повторную постановку в очередь перманентно | Critical | ✅ Исправлено | Во всех 4 процессорах (`MedicalDocumentExtractionProcessor`, `MedicationEnrichmentProcessor`, `LabAnalyteEnrichmentProcessor`, `VisitMedicationEnrichmentProcessor`) добавлен `MaxAttempts` const + `catch`-блок теперь ставит `Status=Failed`, если `job.Attempts >= MaxAttempts` |
| C4 | Гонка «последний админ» — два одновременных выхода/выгона двух разных последних админов могли оба пройти проверку `adminCount <= 1`, семья остаётся без управления навсегда | Critical | ✅ Исправлено | `MembershipService`/`AccountService` — новый `CountActiveAdminsLockedAsync`: под Postgres лочит строки активных админов через `FOR UPDATE` внутри транзакции; под SQLite (только тесты — `FOR UPDATE` там не парсится) — обычный `CountAsync`, т.к. SQLite и так сериализует писателей на уровне файла БД |
| H1 | N+1 на `GET .../conclusion` — заключение врача с N препаратами делало до 3×N SQL-запросов на КАЖДЫЙ просмотр экрана | High | ✅ Исправлено (со второй попытки — см. врезку выше) | `KbLookupService.LookupExactManyAsync` — один batch-запрос `WHERE NormalizedName = ANY(@names)` на все названия заключения разом; промахи по-прежнему падают на старый поштучный каскад (алиас/нечёткое совпадение). Без кэша — живой поиск на каждое чтение сохранён намеренно (см. докстринг класса) |
| H2 | `POST /api/medications/ocr` — единственный путь к LM Studio без дисциплины «один запрос одновременно», в обход `WorkerCount=1` очередей | High | ✅ Исправлено | Новый singleton `LmStudioConcurrencyGate` (`SemaphoreSlim(1,1)`), встроен в `LmStudioJsonClient` вокруг самого HTTP-вызова — защищает разом все пути (OCR, извлечение, суммаризация), не только OCR |
| H3 | 3 места с check-then-insert под уникальным индексом БЕЗ `catch(DbUpdateException)` — гонка = необработанный 500 вместо мягкого отказа | High | ✅ Исправлено | `PushSubscriptionService.SubscribeAsync`, `MedicationSearchCacheService.RecordSearchAsync`, `TelegramLinkService.ConfirmAsync` (username-автоподстановка) — приведены к паттерну detach-и-переиграть, уже используемому в `NotificationSendingService.AddIfNewAsync` |
| H4 | Ночной `ReminderScanJob` грузил таблицы `Birthdays`/`FamilyDependents`/`FamilyMembers` целиком, без учёта окна предупреждения | High | ✅ Исправлено | Новый `MonthsInWindow(today, warningDays)` — SQL-предфильтр `WHERE monthsInWindow.Contains(Date.Month)` перед материализацией во всех трёх сканах; точная граница (`daysUntil`, перенос 29 февраля) осталась без изменений в памяти |
| H5 | Зарегистрированный, но нигде не используемый `FamilyRoleHandler`/`FamilyRoleRequirement` — создаёт ложное впечатление декларативной авторизации | High | ✅ Исправлено | Файлы удалены, регистрация в `Program.cs` убрана, комментарий в `IFamilyAccessService.cs` исправлен (был: "предпочтительнее resource-based через FamilyRoleHandler" — реальной альтернативы никогда не было) |
| H6 | Экспорт данных пользователя (`GET /api/account/export`) буферизовал весь zip (включая расшифрованные вложения) в `MemoryStream` без верхнего предела | High | ✅ Исправлено | `AccountService.WriteExportZipAsync` — промежуточный `MemoryStream` заменён на временный файл (`Path.GetTempFileName()` + `FileStream`), удаляется в `finally` |
| M1 | `LmStudioJsonClient` ловил `TaskCanceledException` от ЛЮБОЙ причины (клиентский таймаут И отмена нашим `ct`) как один бизнес-исход — Hangfire не ретраил задачу, прерванную остановкой хоста | Medium | ✅ Исправлено | `catch` дополнен условием `!ct.IsCancellationRequested` — отмена вызывающим пробрасывается дальше как есть, ловится только "внутренний" таймаут |
| M3 | `TokenService.RefreshAsync` — выпуск новой сессии и отзыв старой двумя раздельными `SaveChangesAsync` без общей транзакции | Medium | ✅ Исправлено | Обёрнуто в `BeginTransactionAsync`/`CommitAsync` |
| M5 | `ConsentService.AddIfMissingAsync` — на гонке уникального индекса звал `db.ChangeTracker.Clear()`, сбрасывая ВСЕ отслеживаемые изменения разделяемого scoped-контекста, не только свою сущность | Medium | ✅ Исправлено | Заменено на точечный `db.Entry(consent).State = EntityState.Detached` |
| M8 | `TelegramApiHealthCheck` — два раздельных static-поля (`_cachedAt`/`_cached`), быстрый путь читал их вне семафора без `volatile`, потенциально рваная пара при параллельных пробах | Medium | ✅ Исправлено | Объединены в один immutable `record CachedProbe(Result, CachedAt)`, хранится в `volatile` ссылочном поле — пара читается/пишется атомарно как одно целое |
| M9 | `/dev/trigger-*` (мутирующие POST) лежат вне `/api` — глобальный CSRF-гейт их не видит, исключение получалось случайно, не по замыслу | Medium | ✅ Исправлено | Добавлен явный `.DisableAntiforgery()` с комментарием (функционально ничего не меняет — `app.UseAntiforgery()` в проекте не подключён вовсе, это чисто документирующая аннотация, тот же паттерн, что у `AttachmentEndpoints`) |
| M2 | `EnrichmentQuotaService` — месячная квота на платный поиск проверяется 3×`CountAsync` без резервирования, конкурентные воркеры могут оба пройти проверку | Medium | ⏳ Не сделано | Сегодня замаскировано `WorkerCount=1` на очереди `enrichment` (один процесс) — сломается только при горизонтальном масштабировании этой очереди, которого сейчас нет и не планируется (LM Studio — один ноутбук физически) |
| M4 | Счётчики `FailedLoginAttempts`/`EmailVerificationCode.Attempts` — read-modify-write без блокировки, параллельный брутфорс может недосчитаться до порога | Medium | ⏳ Не сделано | Defense-in-depth поверх УЖЕ работающего IP rate limiter (policy `auth`/`auth-code`) — тот не изменяется этой находкой. Отложено: ниже ценность/риск, чем закрытые пункты |
| M6 | `AdminStatsService.GetOverviewAsync` — ~13 последовательных `CountAsync` вместо `FILTER`-агрегатов | Medium | ⏳ Не сделано | Админ-панель, низкий трафик — чистая оптимизация, не корректность |
| M7 | `NotificationSendingService.NotifyAsync` — до 3 round-trip'ов на получателя без батчинга | Medium | ⏳ Не сделано | Размер семьи в продукте маленький — абсолютная стоимость низкая |
| M10 | `MailKitSmtpEmailSender` — неатомарный check-then-increment суточного лимита провайдера | Medium | ⏳ Осознанно не трогали | Уже задокументировано в самом классе как допущение "single-instance деплой" (ADR-0001) — не новая находка, действий сверх документации не требуется |
| L1–L5 | Блокирующие вызовы при старте хоста (`GetAwaiter().GetResult()`+`Thread.Sleep`, до ~10с), `KafkaHealthCheck` игнорирует `CancellationToken`, мягкий лимит "25 семей", `AddDbContext` без пулинга, `WorkerCount=1` — гарантия корректности, но процесс-локальная | Low | ⏳ Не сделано | Ни один не создаёт риска при текущей топологии деплоя (один инстанс API, один LM Studio) — см. полный текст в отчёте-артефакте, если понадобится вернуться |

## Что осталось и почему (кратко)

Всё **Critical** и всё **High** закрыто. Из **Medium** взято 6 из 10 — те, что либо реальный риск
данных/корректности (M1, M3, M5, M8, M9), либо дешёвый и безопасный фикс. Не взяты: M2/M4 —
второй рубеж защиты поверх уже работающего первого (WorkerCount=1 и IP-лимитер соответственно),
M6/M7 — чистая производительность без риска для корректности, при низком трафике соответствующих
путей, M10 — уже осознанно принятый и задокументированный трейд-офф, не новая находка. **Low**
целиком не тронуто — ни один пункт не создаёт риска при нынешней топологии (один процесс API,
один LM Studio за WireGuard); стоит вернуться к ним, если топология изменится (горизонтальное
масштабирование API/очередей).

## Изменённые файлы (для истории — детали в `git log`/`git diff`)

```
src/FamilyHub.Api/Features/Account/AccountService.cs
src/FamilyHub.Api/Features/Auth/TelegramLinkService.cs
src/FamilyHub.Api/Features/Consents/ConsentService.cs
src/FamilyHub.Api/Features/Invites/InviteService.cs
src/FamilyHub.Api/Features/Members/MembershipService.cs
src/FamilyHub.Api/Features/Push/PushSubscriptionService.cs
src/FamilyHub.Api/Program.cs
src/FamilyHub.Infrastructure/Auth/Jwt/TokenService.cs
src/FamilyHub.Infrastructure/Authorization/FamilyRoleHandler.cs        (удалён)
src/FamilyHub.Infrastructure/Authorization/FamilyRoleRequirement.cs    (удалён)
src/FamilyHub.Infrastructure/Authorization/IFamilyAccessService.cs
src/FamilyHub.Infrastructure/LmStudio/LmStudioConcurrencyGate.cs       (новый)
src/FamilyHub.Infrastructure/LmStudio/LmStudioJsonClient.cs
src/FamilyHub.Infrastructure/Notifications/ReminderScanJob.cs
src/FamilyHub.Modules.Medical/Enrichment/MedicationEnrichmentProcessor.cs
src/FamilyHub.Modules.Medical/Enrichment/MedicationSearchCacheService.cs
src/FamilyHub.Modules.Medical/Enrichment/VisitMedicationEnrichmentProcessor.cs
src/FamilyHub.Modules.Medical/Extraction/ExtractionQueryService.cs
src/FamilyHub.Modules.Medical/Extraction/LabAnalyteEnrichmentProcessor.cs
src/FamilyHub.Modules.Medical/Extraction/MedicalDocumentExtractionProcessor.cs
src/FamilyHub.Modules.Medical/Kb/KbLookupDtos.cs
src/FamilyHub.Modules.Medical/Kb/KbLookupService.cs
src/FamilyHub.TelegramBot/Health/TelegramApiHealthCheck.cs
tests/FamilyHub.UnitTests/Features/Invites/InviteServiceTests.cs       (assertion на NewContext(),
                                                                          см. врезку про stale-read
                                                                          после ExecuteUpdateAsync)
```

Изменения на момент написания файла **не закоммичены** — рабочая директория, `git diff --stat`
подтверждает список выше.
