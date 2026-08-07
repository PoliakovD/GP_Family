# `FamilyHub.Modules.Medical`

Два разных класса ресурсов в одном модуле — стоит держать в голове их разницу при любых
изменениях, это центральное архитектурное решение проекта (раздел 4/6 брифа).

## Аптечка (`Medications/`) — семейный ресурс

`MedicationService` — простой CRUD по образцу: реализует `IFamilyOwned`, видна всем активным
членам семьи (`FamilyRole.Member` и выше), `Update`/`Delete` сначала грузят сущность по `Id`,
**затем** проверяют доступ к её `FamilyId` (никогда не доверяем `familyId` из URL без сверки с
реальным `FamilyId` записи). `ExpiryDate` (`DateOnly?`) — единственное поле, читаемое
`ReminderScanJob` для оповещений о сроке годности.

Маршруты: `GET/POST /api/families/{familyId}/medications`, `PUT/DELETE /api/medications/{medicationId}`.

## Анализы и посещения врачей (`MedicalRecords/`) — персональный ресурс с двухуровневым шарингом

Одна таблица `MedicalRecord` на оба вида — дискриминатор `Kind` (`Analysis`/`DoctorVisit`, не
шифруется, фильтруется прямо в SQL). Раздел «Врачи» на фронте — плоский список посещений, не
отдельный справочник врачей: `Doctor` остаётся обычной строкой записи (для анализа — «кто
назначил», для посещения — врач/специальность). Весь контур доступа — шаринг, скрытие, аудит,
вложения — общий для обоих `Kind`, изменений не потребовал.

`MedicalRecordService` — **не** использует `IFamilyOwned`/`IFamilyAccessService.HasRoleAsync`
для проверки видимости самой записи (только для проверки «состоишь ли ты в семье, которой
шаришь» при `ShareWithFamilyAsync`). Видимость считается явным предикатом
`VisibleRecordsQuery(userId, kind: null)` — опциональный `kind` фильтрует по виду записи, не
меняя сам предикат видимости:

```
видно, если: ты владелец
           ИЛИ (твои записи расшарены этой семье
                И ты в ней активный член
                И эта конкретная запись не скрыта именно от неё)
```

`GetVisibleRecordsAsync`/`SearchAsync` принимают опциональный `MedicalRecordKind? kind` —
`GET /api/medical-records?kind=analysis|visit` и `SearchService` (источники `Record`/`Visit`)
передают его насквозь, чтобы `types=visit` не расшифровывал вообще ни одного анализа (и наоборот) —
это не косметика, а тот же принцип экономии, что и у остальных источников `SearchService`.

Заготовка под будущий OCR-конвейер (задачи 5.2/5.3, `.claude/plans/medical-platform/stage/stage-5`):
`MedicalRecord.ExtractedDataJson` (`[Encrypted]`) + `ExtractionStatus`, интерфейс
`IMedicalDocumentExtractor` в `Extraction/` с `NullMedicalDocumentExtractor` по умолчанию (по
образцу `IMedicationSearchProvider`/`NullMedicationSearchProvider` ниже). Ни очереди, ни
эндпоинта, вызывающего этот интерфейс, ещё нет — только контракт, чтобы не дёргать схему БД
второй раз, когда конвейер будет реализован.

Два уровня шаринга, реализованные как отдельные таблицы (не флаги на самой записи):

- **Уровень 1** — `FamilyMedicalShare`: владелец одним действием открывает **все** свои анализы
  выбранной семье (`ShareWithFamilyAsync`/`UnshareFamilyAsync`). Отключение шаринга **не**
  чистит `MedicalRecordHidden` (намеренно — раздел "инвариант 5": при повторном включении
  шаринга точечно скрытое должно остаться скрытым).
- **Уровень 2** — `MedicalRecordHidden`: точечно скрыть **конкретную** запись от **конкретной**
  уже расшаренной семьи (`HideFromFamiliesAsync`/`UnhideFromFamiliesAsync`). Можно скрыть
  только от семей, которые уже есть в пересечении «расшарено владельцем» ∩ «семьи, куда скрывают»
  (`CreateAsync` дополнительно пересекает с «активные семьи владельца» — `HideFromFamilyIds`
  при создании записи проходит через тройное пересечение, см. код).

Управление шарингом/скрытием — **только владелец записи** (`record.OwnerUserId != ownerUserId →
Forbidden`), даже `Admin` семьи не может вмешаться. Это и есть инвариант 2 из брифа.

Маршруты: `GET/POST /api/medical-records`, `POST /api/medical-records/share`,
`POST /api/medical-records/unshare`, `POST /api/medical-records/{recordId}/hide`,
`POST /api/medical-records/{recordId}/unhide`.

## Вложения (`Attachments/`)

`AttachmentService` — метаданные в БД (`FileAttachment`), сам файл — в `IFileStorage`
(MinIO, единственная реализация — см. `infrastructure.md`). У вложения **нет собственной
видимости** — она наследуется от родителя через `OwnerType`+`OwnerId`:

- `OwnerType.MedicalRecord` → видимость через `MedicalRecordService.IsVisibleToAsync` (та же
  логика двухуровневого шаринга, общая для анализов и посещений врачей).
- `OwnerType.Medication` → видимость через `IFamilyAccessService.HasRoleAsync` на `FamilyId`
  лекарства (`HasMedicationAccessAsync`).

Загружать вложение может только владелец записи (`UploadForMedicalRecordAsync`) — тот же барьер,
что и для шаринга. Объектный ключ — `StorageKeyFactory.Create(attachmentId)`, полностью
непрозрачный (`blobs/{a}/{b}/{attachmentId}`), никакой связи с `recordId`/видом записи в ключе
нет — администратор хранилища видит только набор несвязанных шифроблобов. Скачивание — только
через собственный API-эндпоинт с HMAC-подписанной ссылкой (TTL 5 минут, `GetPresignedUrlAsync`),
не прямая ссылка на хранилище.

Маршруты: `POST /api/medical-records/{recordId}/attachments` (multipart `file`),
`GET /api/medical-records/{recordId}/attachments` (список, доступ — тот же
`IsVisibleToAsync`, что и у самой записи, аудит-запись при просмотре чужой расшаренной записи),
`GET /api/attachments/{attachmentId}/url` → `{ url }`.

## OCR (`Ocr/`) — оцифровка медикамента по фото

`MedicationOcrService` — вызывает `ILmStudioJsonClient` (`FamilyHub.Infrastructure.LmStudio`,
переименован из `ILmStudioVisionClient` на этапе 4, когда клиент стал использоваться и для
чисто текстовых запросов — суммаризация, см. ниже) с русским system-промптом, просящим
строго один JSON-объект `{ name, expiryDate, fields[] }`. Фото никогда не сохраняются —
используются только в рамках одного запроса. `POST /api/medications/ocr` синхронный (блокирует
запрос на время локального инференса, до `LmStudio:TimeoutSeconds`), возвращает 200 даже при
неудачном распознавании (бизнес-исход, не серверная ошибка).

## Справочник + AI-конвейер обогащения (`Kb/`, `Enrichment/`) — этап 4

Наполняет `kb.global_medications_kb` (задача 2.6) — обезличенный общий справочник препаратов
(назначение, форма выпуска, хранение, влияние на вождение). Конвейер: сохранение медикамента →
нормализация имени → каскадный поиск в справочнике → при промахе/неуверенном совпадении —
фоновая задача Hangfire → веб-поиск по доверенным РФ-источникам → суммаризация локальным Qwen →
запись в справочник → push пользователю. См. [ADR-0005](../../docs/adr/0005-medication-enrichment-egress.md)
для обоснования исходящего вызова и [stage-4.md](../plans/medical-platform/stage/stage-4.md) для истории задачи.

- **`MedicationNameNormalizer`** (`FamilyHub.Infrastructure.Search`) — чистая функция:
  «Парацетамол 400мг таб. №20» → «парацетамол» (снимает дозировку/фасовку/форму выпуска,
  чинит латинские гомоглифы в смешанных словах). Ключ дедупликации `NormalizedName` и точка
  входа каскада поиска.
- **`KbLookupService`** — каскад точное совпадение → алиас (торговое название) → нечёткое
  (триграммы + tsvector, пороги строже общего поиска: `0.55` автопривязка, `0.35` кандидат на
  подтверждение). `Aliases` и `search_vector` — Postgres `text[]`/`tsvector`, как и в
  `SearchService`, намеренно вне EF-модели (не проходят кроссплатформенно в SQLite-юнит-тестах).
- **`EnrichmentRequestService`** (за интерфейсом `IEnrichmentRequestService` — реализация ходит
  raw SQL к Postgres-специфичным функциям, юнит-тесты `MedicationService` подставляют заглушку) —
  вызывается из `MedicationService.CreateAsync`/`UpdateAsync` сразу после сохранения. `RequestAsync`
  прерывается на уверенном `Hit`; `RequestRefreshAsync` (ручное «Уточнить в справочнике») — нет.
  Дедуп на уровне БД — частичный уникальный индекс `MedicationEnrichmentJobs.NormalizedName` среди
  `Pending`/`Running` задач.
- **`MedicationEnrichmentProcessor`** — Hangfire-джоба в выделенной очереди `enrichment`
  (`[Queue("enrichment")]`, один воркер — см. `Program.cs`, естественно укладывается в лимит
  Brave free-tier 1 req/s), `[AutomaticRetry(Attempts = 3)]` только на настоящие сбои; ожидаемые
  исходы (нет доверенных источников, квота исчерпана) переводят статус задачи в `Failed`/`Skipped`
  обычным `return`, без ретрая.
- **`IMedicationSearchProvider`** (`FamilyHub.Infrastructure.Enrichment`) — `NullMedicationSearchProvider`
  по умолчанию (наружу не уходит ничего); активный провайдер — `YandexSearchProvider`
  (`Enrichment:Provider=Yandex`, Web Search API `v2/gen/search`/GenSearch, egress через
  `searchapi.api.cloud.yandex.net`, ответ приходит ОБЁРНУТЫМ В МАССИВ — подтверждено живым
  запросом, расходится с примером в документации Yandex). Фильтрация по `TrustedDomains` —
  постфактум по `used`-источникам (пробовали ограничивать сам запрос полем `host` — на практике
  это стабильно давало «Ничего не найдено» даже для доменов, которые находятся без ограничения).
  `BraveSearchProvider` — поддерживаемая альтернатива (`Enrichment:Provider=Brave`), не
  используется по умолчанию. Наружу уходит только нормализованное название препарата — один запрос
  на весь справочник, не по запросу на пользователя (см. ADR-0005).
- **`MedicationSummarizer`** — суммаризация сниппетов локальным Qwen (`ILmStudioJsonClient`,
  текстовая перегрузка без фото; уровень «размышлений» — `LmStudio:ThinkingLevel`, 0-3, см.
  `LmStudioOptions`/`LmStudioJsonClient`). Антигаллюцинационный гейт: пустой `usedSourceIndexes`
  или все поля `null` → запись в справочник отклоняется. Способ применения/дозы (`Usage`, схема v2)
  и противопоказания/рекомендации (`SpecialNotes`) извлекаются в полном объёме, что есть в
  цитируемой инструкции, — это не ограничивается искусственно (продуктовое решение, см. ADR-0005
  п.8); заземление на процитированные доверенные источники остаётся обязательным.
- **`KbWriter`** — единственная точка записи в `kb.global_medications_kb` (тот самый «writer-сервис
  этапа 4», которого явно ждёт `KbIsolationGuardTests.PersonalContext_CannotBeStoredInKbRow»).
  Двойная проверка изоляции: структурная (EF-модель без персональных полей) + на уровне значений
  (GUID/e-mail/длинные цифровые последовательности/персональные ключевые слова в тексте payload).
  Upsert по `NormalizedName` (`ON CONFLICT ... DO UPDATE`), слияние `Aliases`.

Маршруты: `GET /api/kb/medications` (поиск/листинг для UI), `GET /api/kb/medications/{id}`
(карточка), `GET /api/medications/{medicationId}/kb` (статус обогащения конкретного медикамента),
`POST /api/medications/{medicationId}/kb/refresh` (ручной рефреш).

## Wiring модуля (`MedicalModule.cs`)

`AddMedicalModule()` регистрирует все сервисы модуля (`Medkit`/`Medication`/`MedicalRecord`/
`Attachment`/`MedicationOcr`/`Search` + `IMedicalDocumentExtractor` (заготовка под 5.2/5.3,
Null-реализация по умолчанию) + этап-4 `KbLookupService`/`KbCatalogService`/
`MedicationKbStatusService`/`KbWriter`/`MedicationSummarizer`/`IEnrichmentRequestService`/
`MedicationEnrichmentProcessor`) в DI; `MapMedicalModule()` вызывает все `Map*Endpoints()`, вся
группа — под `ConsentRequiredFilter`. Подключается из `FamilyHub.Api/Program.cs`, не имеет
обратной зависимости на `FamilyHub.Api` или `FamilyHub.Modules.Birthdays`.
