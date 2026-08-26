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

OCR-конвейер (ветка `medicalrecords`, редизайн v2 — после `ReworkPersonIdentity`) реализован:
диспетчер форматов `FamilyHub.Infrastructure.Documents.IDocumentTextExtractor` (текстовый слой
PDF/офисные форматы напрямую через PdfPig/NPOI; vision-OCR через `ILmStudioJsonClient` только для
фото и PDF-сканов без текстового слоя, отрендеренных PDFium/`PDFtoImage`) → доменная структуризация
`LmStudioMedicalDocumentExtractor` (`Extraction/`, два промпта — анализ/выписка; для анализа модель
также отдаёт `specimen`/`documentDate`/`suggestedTitle` уровня документа, не индикатора —
дешевле для модели, чем спрашивать на каждый показатель) → нормализация (`LabAnalyteNormalizer`) →
каскадный поиск в `kb.global_lab_analytes_kb` (`LabAnalyteKbLookupService`).

**v2: задача на ЗАПИСЬ, не на вложение.** Кнопка «Распознать» — одна на записи (не на файле),
`POST /api/medical-records/{recordId}/extract` ставит ОДНУ задачу
(`MedicalDocumentExtractionJob`, дедуп-индекс теперь по `MedicalRecordId`), которая
последовательно обрабатывает ВСЕ ещё не распознанные вложения (`FileAttachment.ExtractedAt`
— null у необработанных, проставляется сразу после чтения файла, до финального сохранения —
повтор клика после сбоя не гоняет OCR по уже прочитанным файлам). Показатели из разных файлов
МЕРЖАТСЯ upsert'ом по `(MedicalRecordId, AnalyteKey, Specimen)`, не blanket-delete — повторный
клик «Распознать» после добавления нового файла не стирает результаты уже разобранных. Один
проход `LabSummarizer` по полному смерженному набору — не по каждому файлу.

**Каскад референса** (`FamilyHub.Domain.Enums.RefSource`, `IndicatorFlagCalculator`):
1. `Blank` — референс из самого бланка (или из ручной правки, см. ниже) — высший приоритет.
2. `KbFixed` — фиксированный диапазон `GlobalLabAnalyteKb.PayloadJson.refRanges`, подобранный по
   полу (`Gender`, из `User`/`FamilyDependent` — identity rework сделал это возможным) и возрасту
   (`PatientIdentityResolver.ResolveAsync`, общий для процессора и джобы ниже).
3. `KbCalculated` — фиксированного диапазона нет, но у KB-записи есть `CalculationInstructions`
   (словесная методика — например, клиренс креатинина): `PatientReferenceCalculator` просит
   локальную LLM посчитать low/high под конкретного пациента (возраст+пол; вес/рост НЕ
   запрашиваются — профильных полей под них нет, осознанное решение при планировании v2), строго
   в единице измерения бланка — несовпадение единиц отбрасывает результат.
4. `None` — промах KB целиком → `LabAnalyteEnrichmentRequestService` ставит `LabAnalyteEnrichmentJob`
   в очередь `enrichment` (дедуп — частичный индекс по `NormalizedName`); `Flag=Unknown` до тех пор.

Показатель хранится в `medical.LabIndicators` (`AnalyteKey`/`Flag`/`RefSource`/`Specimen`
plaintext — по ним поиск/тренд/группировка, значения/референсы `[Encrypted]`).
**`Specimen`** (`SpecimenType`: Blood/Urine/Stool/VaginalSwab/Saliva/Other/Unknown) — биоматериал,
часть ключа группировки вместе с `AnalyteKey` везде (`GET /api/indicators`,
`GET /api/indicators/{analyteKey}/{specimen}`, upsert-ключ) — без него лейкоциты крови и мочи
слились бы на одном графике. Редактируется вручную (`PUT /api/indicators/{id}`, только владелец —
исправление ошибок OCR; ref-поля из запроса становятся новым `RefSource.Blank`, Flag
пересчитывается тем же компаратором).

**Дозаполнение задним числом.** Когда `LabAnalyteEnrichmentProcessor` наполняет
`kb.global_lab_analytes_kb` (после промаха выше), он ставит `RecalculateIndicatorFlagsJob` —
проходит по `LabIndicators` с `RefSource=None` и тем же `AnalyteKey`/`KbAnalyteId`, прогоняет
каскад заново. Без этого пользователь, распознавший анализ первым (когда KB ещё пуст), навсегда
остался бы с `Unknown`.

Событие `MedicalDocumentExtractedEvent` (только счётчики) → push владельцу. Выписки врача
(`Kind=DoctorVisit`) — `VisitConclusion` в `ExtractedDataJson`, без графика приёма/календаря (вне
объёма). Оркестрация — `MedicalDocumentExtractionProcessor`, Hangfire-очередь `extraction`, один
воркер (LM Studio — один ноутбук за WireGuard). Чтение — `ExtractionQueryService`
(`GET .../extraction` — включает `totalFiles`/`processedFiles` для прогресса «файл N из M»,
`.../indicators`, `.../summary`, `.../conclusion`, `GET /api/indicators`,
`GET /api/indicators/{analyteKey}/{specimen}`), видимость — тот же предикат, что у записи.

Конвейер обогащения `kb.global_lab_analytes_kb` — зеркало `MedicationEnrichmentProcessor`:
`IMedicationSearchProvider.SearchAsync(name, WebSearchTopic.LabAnalyte)` (отдельный список
доверенных доменов — `EnrichmentOptions.AnalyteTrustedDomains`: helix.ru/invitro.ru/gemotest.ru/
kdlmed.ru/cmd-online.ru) → `LabAnalyteKbSummarizer` (тот же антигаллюцинационный гейт; v2 —
промпт также просит `sex` на каждый диапазон и `calculationInstructions`) → `LabAnalyteKbWriter`
(upsert, `KbIsolationGuard`). Месячная квота — общая на оба конвейера (`EnrichmentQuotaService`).
См. дополнение к [ADR-0005](../../docs/adr/0005-medication-enrichment-egress.md).

**`MedicalRecord` — структура (v2).** `PersonName` убран целиком — идентичность пациента
выражается только через `FamilyDependentId`/`TargetUserId`/владельца, отображаемое имя резолвится
на чтение (`MedicalRecordService.ResolvePersonNamesAsync`, батч на список — не N+1), не хранится
(подопечный/участник может переименоваться в профиле, отображение это подхватывает). Добавлено
`Title` (`[Encrypted] string?`) — короткое название ("Общий анализ крови"), из
`ExtractionResult.SuggestedTitle` (модель видит шапку бланка) либо введено вручную; не
затирается повторным распознаванием, если уже задано. `RecordDate` по умолчанию — дата создания
(фронт), может быть переопределена `ExtractionResult.DocumentDate`, если бланк её печатает.
`Doctor` — теперь с автоподсказкой (`GET /api/medical-records/doctors`, in-memory `Distinct()`
по СВОИМ записям пользователя после расшифровки — `Doctor` `[Encrypted]`, SQL DISTINCT по
шифротексту бессмыслен, ADR-0002).

Фронт (`FamilyHub.Web`): одна кнопка «Распознать» на записи (не на вложении) —
`medical-records-panel.component.ts` показывает прогресс «файл N из M», после `Completed` —
таблицу показателей (специмен, значение, норма, бэйдж «ИИ» для `RefSource.KbCalculated`,
инлайн-правка через карандаш) + LLM-резюме (Kind=Analysis) либо заключение врача
(Kind=DoctorVisit, `GET .../conclusion`). Форма создания — только выбор пациента из
self/подопечный/участник (без свободного поля имени), дата по умолчанию сегодня, врач — `<input
list>`/`<datalist>` с подсказками (в проекте нет typeahead-компонента, нативный datalist —
осознанный выбор). Вкладка «Показатели» хаба «Здоровье» (`indicators-tab`, `/health/indicators`)
группирует по `(analyteKey, specimen)` — история/спарклайн (`shared/sparkline`, inline SVG) по
клику. Специмен-подписи — общий `shared/util/specimen.ts`.

Лимиты вложений мед-записи — `AttachmentUploadOptions` (env `Attachments__MaxFileSizeBytes`/
`Attachments__MaxFilesPerRecord`, дефолт 5 МиБ/8 файлов на запись): проверка размера — как раньше,
проверка количества — `AttachmentAccessResult.TooManyFiles` → 409. Фронт грузит лимиты заранее
(`GET /api/attachments/limits`) для предвалидации и подписи «осталось N из 8»; инпут поддерживает
`multiple` + `accept`. `AttachmentDto.ExtractedAt` — фронт определяет по нему, есть ли у записи
ещё нераспознанные файлы (показывать ли кнопку «Распознать» вообще).

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
