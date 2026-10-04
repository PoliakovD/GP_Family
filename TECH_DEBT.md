# Технический долг

Зафиксированные ограничения и нерешённые мелочи, обнаруженные по ходу разработки.
Не блокируют текущий v1, но стоит учитывать при дальнейшей работе. Актуализировано 2026-09-28.

## Закрыто / устарело (оставлено как история)

### 1. `dotnet ef` и `ASPNETCORE_ENVIRONMENT` — закрыто
Раньше `dotnet ef`, запущенный из `src/FamilyHub.Api`, резолвил строку подключения на БД `familyhub`
независимо от среды. Сейчас у Infrastructure есть `DesignTimeDbContextFactory` (строка — из
переменной окружения `FAMILYHUB_CONNECTION_STRING`, есть дефолт), и миграции создаются без запуска
хоста и без БД: `dotnet ef migrations add <Имя> --project src/FamilyHub.Infrastructure`. Для
`database update` на конкретную БД — задавать `FAMILYHUB_CONNECTION_STRING` (или `--connection`).

### 2. `docker-compose.yml` и имя dev-БД — закрыто
Имя базы теперь одно на всех: `POSTGRES_DB` из `.env` (`dev.env.example` — `familyhub`) попадает и в
контейнер Postgres, и в `ConnectionStrings__Postgres` сервиса `api`; строки подключения в
`appsettings*.json` нет вовсе.

### 11. Фронт: нет юнит-тестов, e2e покрывает не всё — закрыто (кроме Telegram initData e2e, см. ниже)
Юнит-тестов у `FamilyHub.Web` не было вовсе, `ng test` не запускал ничего. Подключён
`@angular/build:unit-test` (Vitest, jsdom) — новый architect-таргет `test` в `angular.json`,
`npm test` в `package.json` больше не висящий скрипт. Первые спеки: `auth.guards.spec.ts`
(authGuard/consentGuard/profileGuard — TestBed.runInInjectionContext + фейковые AuthService/
TelegramService/Router), `intake-labels.spec.ts` (включая регрессию на `todayLocal()`, TECH_DEBT.md
#18), `patient-options.spec.ts`. `ci.yml` теперь гоняет `npm test` на каждый push/PR. Побочный
эффект перехода на `@angular/build` (см. #15) — та же смена пакета дала доступ к builder'у
`unit-test`, который нельзя было взять из старого `@angular-devkit/build-angular` в этой версии.
Билдер помечен самим Angular как EXPERIMENTAL — приемлемо для первых спеков, но не повод класть на
него что-то критичное без проверки при следующем мажоре Angular.

`e2e.yml` теперь запускается и на `pull_request`, если правка касается `src/FamilyHub.Web/**` или
`e2e/**` (paths-фильтр — чтобы не платить минуты за чисто бэкендные PR), не только на push в
`master`/вручную.

PWA-вход email+паролём (`17-pwa-email-login.spec.ts`) — сквозной сценарий регистрации: код из
письма недоступен без реального ящика, поэтому добавлен `GET /dev/last-otp?email=` (гейт тот же
`DevTools:DevEndpointsEnabled`, что и весь `DevEndpoints`) — `DevEmailCapture`, in-memory перехват
письма внутри `LoggingEmailSender` (сам класс — уже dev-заглушка, используется только когда ни один
реальный провайдер не настроен; тот же приём, что `CapturingEmailSender` в интеграционных тестах,
только без доступа к DI реального процесса — e2e гоняет настоящий `dotnet run`, не
`WebApplicationFactory`).

Telegram initData e2e осталось не тронуто — HMAC-валидация (`TelegramInitDataValidator`) и
lookup-only гейт (`TelegramMiniAppAuthenticationHandler`) уже покрыты юнит-тестами (включая крипто-
корректность подписи), а `Telegram:BotToken` в e2e-стенде (`e2e/support/env.ts`) пуст — валидатор
отклоняет ЛЮБОЙ initData без него по построению. Довести до e2e означало бы завести отдельный
тестовый токен в общий бутстрап стенда (используется всеми сценариями) и подделывать
`window.Telegram.WebApp` в браузере — отдельная, более рискованная для общей стабильности e2e
задача, не сделана в этом заходе.

### 5. ИИ недоступен: немые шаги конвейера после структурирования — закрыто
Очередь «ждём ИИ» (ADR-0013) покрывала постановку распознавания, резюме, отложенный биоматериал и
обогащение, но шесть шагов **внутри** прогона распознавания после структурирования показателей —
`SpecimenResolver`, `AnalysisTitleGenerator`, `AnalyteSubjectResolver`, `OcrNameCorrector`,
`PatientReferenceCalculator`, `QualitativeNormJudge` — при сбое ИИ молча возвращали пусто/`null`
независимо от причины: запись становилась «Готово» без источника/названия/норм по полу и возрасту и
больше не пересматривалась, даже если LM Studio всего лишь не ответил в этот раз.

`LmStudioJsonResult.IsTransient` уже отличал техническую недоступность от отказа модели по
содержанию (см. `ILmStudioJsonClient`/ADR-0013) — шести шагам просто не хватало веток для него.
Теперь при `IsTransient: true` каждый из них бросает `LmStudioUnavailableException`; `!IsTransient`
(модель ответила, но не смогла/отказалась) — поведение не изменилось (тихий пусто/`null`, как
раньше). `MedicalDocumentExtractionProcessor.RunAsync` уже ловил это исключение и переводил задачу в
`Pending`+`WaitingForAi` (без траты попытки) для места, где оно бросалось раньше (классификатор вида
документа, до структурирования) — этот же catch теперь ловит исключение и из шести новых мест,
часть из которых (`PatientReferenceCalculator`/`QualitativeNormJudge`) вызывается уже ПОСЛЕ того, как
часть показателей записи добавлена в `DbContext` в этом же прогоне: `RunAsync` дополнительно вызывает
`db.ChangeTracker.Clear()` (с повторным `Attach` job'ы) перед сохранением нового статуса — иначе
ближайший `SaveChangesAsync` заодно сохранил бы недоделанный набор показателей.

`RecalculateIndicatorFlagsJob`/`RecomputeIndicatorFlagsBackfillJob` (два других вызывающих
`PatientReferenceCalculator`/`QualitativeNormJudge`) не потребовали правок — у обоих одна
`SaveChangesAsync` в конце цикла по всем показателям, поэтому необработанное исключение и раньше не
могло оставить частичный набор в БД; `[AutomaticRetry(3)]` Hangfire теперь просто даёт транзиентному
сбою реальный повтор вместо того, чтобы прежде тихо поглощать его.

Регрессия закрыта тестами `*_TransientFailure_Throws` в юнит-тестах каждого из шести шагов;
сам механизм `WaitingForAi` уже покрыт `AiUnavailableApiTests.RequestExtraction_WhenAiDown_*`.

### 6/7. `LmStudio:TimeoutSeconds` = 1000 для всех вызовов — закрыто
«Молчащий» туннель (пакеты теряются, а не отбиваются) держал любой вызов LM Studio, включая
короткий текстовый промпт или синхронный `POST /api/medications/ocr`, до полного
`LmStudio:TimeoutSeconds` (1000 c) — около 17 минут на один вызов, занимая воркер очереди
`extraction`/`enrichment` и `LmStudioConcurrencyGate`, а для OCR ещё и держа открытым HTTP-запрос
пользователя.

`ILmStudioJsonClient.ExtractJsonAsync` получил параметр `shortTimeout` (тот же приём, что уже был у
`suppressThinking`): при `true` вызов ограничен отдельным, гораздо более коротким
`LmStudioOptions.ShortCallTimeoutSeconds` (120 c по умолчанию) через линкованный
`CancellationTokenSource` — не подменяет переданный `ct` (реальная отмена вызывающим по-прежнему
отличима от сработавшего короткого таймаута, см. комментарий в `LmStudioJsonClient.
SendChatCompletionAsync`). Полный `TimeoutSeconds` остался только у многостраничного/многофотового
распознавания документа (`LmStudioMedicalDocumentExtractor`) — ему действительно может понадобиться
больше времени. Короткий таймаут применён везде, где вызов — один промпт или OCR нескольких (до
5-8) фото: `DocumentKindClassifier`, `SpecimenResolver`, `AnalysisTitleGenerator`,
`AnalyteSubjectResolver`, `OcrNameCorrector`, `PatientReferenceCalculator`, `QualitativeNormJudge`,
`LegitimacyGuardService`, `AnalytePlausibilityGuardService`, суммаризаторы обогащения
(`LabSummarizer`, `LabAnalyteKbSummarizer`, `MedicationSummarizer`, `GlobalSpecimenKbService`),
`MedicationOcrService` и `VaccinationCertificateOcrService`. Регрессия закрыта тестами
`LmStudioJsonClientTests.ExtractJsonAsync_ShortTimeout_*`.

`LmStudioConcurrencyGate.WaitAsync` НЕ ограничен коротким таймаутом (сознательно): «дешёвый» шаг,
вставший в очередь за уже идущим тяжёлым распознаванием, должен дождаться своей очереди, а не
падать по таймауту только потому, что кто-то другой занял единственный физический инстанс модели.

Осталось открытым (не про таймаут, отдельная архитектурная причина, ADR-0001): фото упаковки/OCR не
хранятся, поэтому `POST /api/medications/ocr` и `POST /vaccinations/certificate/recognize`
по-прежнему синхронные HTTP-эндпоинты, а не задачи в очереди — от 1000 до 120 секунд ожидания,
но не в очередь.

### 9. Флейковый интеграционный тест — закрыто
`AdminStatsApiTests.StatsEndpoint_WithSession_Returns200(path: "/api/admin/stats/system")` изредка
падал на CI с `Npgsql.NpgsqlException … Attempted to read past the end of the stream` из
`Hangfire.PostgreSql` (`monitoring.Queues()`/`FailedCount()` в `AdminStatsService.GetSystemStatsAsync`
— на собственном пуле соединений Hangfire, в стороне от EF/AppDbContext). `GetHangfireStatsWithRetry`
теперь до 2 раз повторяет вызов при `NpgsqlException`/`InvalidOperationException`, каждый раз заново
запрашивая `IMonitoringApi` у `JobStorage.Current` — устаревшее соединение из пула не переиспользуется
повторно, следующее почти всегда рабочее.

### 12. `profileGuard` принимал временный сбой за «профиль не заполнен» — закрыто
`GET /api/auth/me` делил бакет rate-limit `auth` (10/мин на IP) с попытками входа; 429 или сетевой
сбой гард не отличал от «профиль не заполнен» и уводил на `/profile-setup` (тем же приёмом
`authGuard` — на `/login`) уже аутентифицированного пользователя с заполненным профилем. Хуже всего
это било по Telegram-режиму (`authGuard` там не грузит `me()`, поэтому `/me` дёргается на каждой
guard-навигации без кеша) и по NAT с общим IP.

Исправлено: `AuthService.loadMe()` (`auth.service.ts`) теперь различает `meLoadError`
(`'unauthorized'` — настоящий 401, `'transient'` — 429/сеть/5xx); `authGuard`/`profileGuard`
(`auth.guards.ts`) при транзиентном сбое один раз повторяют запрос и, если он всё равно не удался,
не путают это с «не вошёл»/«профиль не заполнен» — пропускают навигацию (страница либо откроется
нормально, либо её запросы получат 401, который разберёт `authInterceptor`). На бэкенде `/me`,
`/refresh`, `/logout(-all)`, `/sessions*` переведены на отдельную, более мягкую политику
`auth-session` (`AuthRateLimitOptions.AuthSessionPermitLimit`, 60/мин на IP по умолчанию) — логин и
обычный трафик сессии больше не делят один лимит. Регрессия закрыта тестом
`AuthRateLimitTests.Me_IsNotLimitedByLoginBruteForceBucket`.

### 18. Флейковый e2e `14-vaccinations.spec.ts` возле полуночи — закрыто
`VaccinationService` сравнивал дату прививки с «сегодня» по `DateTime.UtcNow`, а фронт
(`todayLocal()`) шлёт локальную дату браузера — в часовых поясах восточнее UTC в первые часы после
местной полуночи это давало ложное 400 «Дата не может быть в будущем». Теперь `VaccinationService`
(create/bulk/update/schedule) считает «сегодня» в часовом поясе пользователя, выполняющего действие
(`User.TimeZoneId`, тот же `TimeZones.Resolve`/`DoseScheduleExpander.LocalDate`, что и у курсов
приёма лекарств) — регрессия закрыта тестами `VaccinationServiceTests.CreateAsync_UsesActingUsersTimeZone_NotUtc`
и `..._RejectsDate_InFutureForActingUsersOwnTimeZone`.

Сознательно не тронуто (не было частью этого дефекта, UTC-«сегодня» там не отклоняет запрос, а
только на несколько часов сдвигает окно): `HealthSummaryService.BuildMedkitsAsync` (срок годности
лекарств в аптечке) и `VaccinationReminderJob` (окно DueSoon/CanDo для напоминаний) — там для
подопечных нет своего часового пояса (только `CreatedByUserId`), а неточность на пару часов раз в
сутки не создаёт видимого пользователю бага.

### 15. Опциональные миграции Angular — закрыто
`use-application-builder` выполнена вручную (официальной ng-update схемы для смены ИМЕННО пакета не
нашлось — CLI её не предложил, схема `application`/`dev-server` в `@angular/build` идентична
`@angular-devkit/build-angular`): `angular.json` — builder `@angular/build:application`/
`:dev-server`, `package.json` — `@angular-devkit/build-angular` заменён на `@angular/build` той же
версии. Побочный эффект — `ng build` заметно быстрее (dev-сборка ~70-80 с → ~11 с, новый builder
использует персистентный кэш на `@parcel/watcher`/`lmdb`) и initial-бандл меньше (см. #13: ≈885 кБ →
710 кБ). `router-current-navigation` не нужна — `Router.getCurrentNavigation`/`currentNavigation` в
кодовой базе не используется вовсе. `control-flow-migration` была не нужна и раньше (в шаблонах уже
`@if`/`@for`).

### 13. Бюджет initial-бандла превышен — закрыто (побочный эффект #15)
`ng build` (prod) предупреждал: ≈885 кБ при предупредительном пороге 850 кБ (ошибка — с 1,5 МБ).
Переход на `@angular/build` (#15) сам по себе уронил initial-бандл до 710 кБ — запас ≈140 кБ,
предупреждение больше не показывается. Рост, разумеется, продолжится дальше — ленивые чанки
остаются нужным приёмом на будущее, если бюджет снова станет тесным.

### 19. Показатели «%» и «абс.» одного названия схлопывались в один ключ — закрыто
`LabAnalyteNormalizer.NormalizeAnalyteKey` вырезал скобки, «#» и завершающий «%», поэтому
«Нейтрофилы (общ.число), %» и «Нейтрофилы (NEU#)» давали один `AnalyteKey`: вторая строка бланка
перезаписывала первую, абсолютное значение получало процентные нормы. Теперь маркер абсолютной формы,
потерянный при нормализации («(абс.)», «#», «abs»), возвращается в ключ словом «абс» — тем же, что даёт
бланк с «Нейтрофилы, абс.» вне скобок. Одноимённые строки одного файла без маркера разводятся по единицам
(`PercentAbsoluteSplitter`: непроцентной дописывается «, абс.»). Процентная форма ключ не меняет —
справочник и прогретый кэш остаются валидными; новые ключи «… абс» обогащаются при первом промахе.
Существующие показатели с маркером в скобках перекладываются пересборкой справочника из админки
(`LabAnalyteKbRebuildJob` пересчитывает ключ из сохранённого имени); строки, уже потерянные коллизией
в прошлых распознаваниях, восстанавливаются только повторной загрузкой бланка.

## Открытое

### 3. Регистрация вебхука бота на реальном домене не проверена
`TelegramWebhookRegistrar` (теперь в `FamilyHub.TelegramBot`, ADR-0008) вызывает
`SetWebhook`/`SetChatMenuButton` только если заданы `Telegram:BotToken` и `Telegram:WebhookUrl` — для
локальной разработки это осознанно пропускается. Публичный домен с HTTPS, туннель AmneziaWG и
`setWebhook` против настоящего Telegram проверены только синтетически (маршрут `/bot/webhook`
запросом; `/health/telegram` доказывает egress через туннель, но только вручную). Сайдкар `wg-client`
собирается из `master` upstream (`AMNEZIAWG_GO_COMMIT`/`AWGTOOLS_COMMIT` не закреплены) и не
проверялся против конкретного Amnezia-сервера — см. комментарии в `deploy/wg/Dockerfile`.

Один дефект этого класса уже найден и исправлен по факту использования: `allowedUpdates` в
`SetWebhook` не включал `UpdateType.CallbackQuery`, из-за чего инлайн-кнопки («Привязать»/«Отмена»)
не доходили до бота; тесты синтезируют `Update` и шлют его напрямую, минуя `setWebhook`, поэтому дефект
не ловился (регрессионный тест `TelegramWebhookRegistrarTests`).

### 4. Монетизация и чат/календарь вне объёма v1
Монетизация/лимиты (этап 5 брифа) и чат/календарь (этап 6+) — намеренно вне объёма текущей работы.
Готовые рычаги для тарифов уже есть: суточные лимиты на пользователя
(`ExtractionLimitsOptions`), лимиты вложений, число семей.

### 8. Задачи обогащения справочника удалённых записей остаются
Удаление записи/подопечного/аккаунта чистит задачи распознавания, но задачи обогащения справочника
(общие для справочника) остаются и в глобальном трее показываются без ссылки на запись. Решение
(«отменять Pending-обогащение, порождённое только этой записью») потребует учёта владельца задачи.

### 10. Заморожённые зависимости
См. таблицу в `.claude/research/frontend-toolchain-and-e2e.md`: MassTransit 8.5.x (не 9) и
FluentAssertions 6.12.x (не 8) — коммерческие лицензии; NPOI 2.7.4 ↔ SkiaSharp 2.88 ↔ PDFtoImage 4.x —
обновлять только вместе (NPOI 2.8 требует SkiaSharp 3); ImageSharp 2.1.13 закреплён как патченная версия.
Тесты-зависимости с мажорами позади: NSubstitute, Testcontainers, xunit runner, coverlet.

### 14. `Deploy` — только вручную
Хотфиксы (например, `Caddyfile`) не выкатываются сами: после мержа нужен ручной запуск `Deploy`
(решение зафиксировано в `deploy/DECISIONS.md`). Держать в голове при инцидентах.

### 16. Прививки (ADR-0016): нацкалендарь не выверен клинически, сертификат не протестирован на реальном LM Studio
`VaccineCatalog` — приближение приказа №1122н (возрастные окна округлены до месяцев/дней силами
разработчика, не медиком); перед продакшн-использованием нужна клиническая сверка с актуальной
редакцией. `VaccinationCertificateOcrService` (распознавание фото сертификата) проверялся только на
недоступном ИИ (фоллбэк-путь) — реальная точность сопоставления найденного текста с каталогом
(нормализованное вхождение, без триграмм) на живых фото не оценивалась.

### 17. Хаб «Здоровье» (редизайн навигации, ADR-0017): осознанные упрощения
- «Главный показатель» плитки «Показатели» (`HealthSummaryService.PickHighlight`) — первый вне
  нормы, иначе самый свежий; настоящий выбор «по наибольшему изменению» потребовал бы истории
  каждого отслеживаемого показателя, это было бы дорого ради превью на плитке.
- В сайдбаре у пункта «Аптечка» нет оранжевой точки об истекающих сроках (в отличие от плитки
  хаба, которая её показывает) — понадобился бы ещё один общий стейт-сервис ради одного индикатора;
  сама плитка хаба всё равно отражает это через `/api/health/summary`.
- `GET /api/medical-records?self=true` (старое) продолжает пропускать анализы/визиты, которые
  владелец расшарил моей семье — единственный оставшийся потребитель (`medical-records-panel.
  component.ts`, чип «Я») переведён на новый `subject=me` (`MedicalRecordFilter.MineOnly`), у
  которого этой дыры нет (см. ADR-0017/`MedicalRecordServiceTests.MineOnly_*`). Сам параметр `self`
  на бэкенде оставлен ради уже закешированных у пользователей старых PWA-бандлов — фронт его больше
  не шлёт.
- Плитка «Прививки» ведёт прямо на график пользователя (`/health/vaccinations/people/user/{id}`),
  а не на `/health/vaccinations?person=me` — у обзора прививок нет своего фильтра «на себя», это
  ближайший осмысленный аналог.
