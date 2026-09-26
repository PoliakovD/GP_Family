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

`SearchAsync` принимает опциональный `MedicalRecordKind? kind` — `SearchService` (источники
`Record`/`Visit`) передаёт его насквозь, чтобы `types=visit` не расшифровывал вообще ни одного
анализа (и наоборот) — это не косметика, а тот же принцип экономии, что и у остальных источников
`SearchService`.

**UX-редизайн (после v2): сортировка/фильтры/пагинация.** `GetVisibleRecordsAsync` больше не
принимает голый `kind` — берёт `MedicalRecordFilter` (`Kind`/`From`/`To`/`FamilyDependentId`/
`TargetUserId`/`SelfOnly`/`Doctor`/`Query`/`Page`/`PageSize`) и отдаёт `PagedResult<MedicalRecordDto>`
(дефолт 15/стр., потолок 100). Список сортируется строго по `RecordDate` (было — не
сортировался вообще), `CreatedAt` — только tiebreaker. Два пути выборки: без `Doctor`/`Query` —
целиком в SQL (`Skip/Take`, `CountAsync`); с ними — материализация SQL-отфильтрованного среза,
расшифровка, фильтр/скоринг в памяти, пагинация на C#-стороне (тот же приём, что уже был в
`SearchAsync`/`GetDoctorSuggestionsAsync` — `Doctor`/`Title`/`Description` `[Encrypted]`, SQL по
ним невозможен, ADR-0002). `MedicalRecordDto` несёt три счётчика (`AttachmentCount`,
`UnrecognizedAttachmentCount`, `IndicatorCount`), посчитанных двумя `GroupBy` по id страницы —
фронт больше не делает `GET /attachments` на каждую запись при открытии вкладки (было N+1: до
редизайна `refresh()` грузил вложения для ВСЕХ записей плюс показатели/резюме для всех Ready-
записей сразу, без пагинации — десятки-сотни запросов на открытие). `GET /api/search` получил ту
же пагинацию (`page`/`pageSize`, дефолт 15) — `SearchService.perSourceLimit` растёт с
`page * pageSize` (потолок 200), чтобы глубокая страница не недобрала элементы ни от одного из
шести источников.

OCR-конвейер (ветка `medicalrecords`, редизайн v2 — после `ReworkPersonIdentity`) реализован:
диспетчер форматов `FamilyHub.Infrastructure.Documents.IDocumentTextExtractor` (текстовый слой
PDF/офисные форматы напрямую через PdfPig/NPOI; vision-OCR через `ILmStudioJsonClient` только для
фото и PDF-сканов без текстового слоя, отрендеренных PDFium/`PDFtoImage`) → доменная структуризация
`LmStudioMedicalDocumentExtractor` (`Extraction/`, два промпта — анализ/выписка; для анализа модель
также отдаёт `specimen`/`documentDate`/`suggestedTitle` уровня документа, не индикатора —
дешевле для модели, чем спрашивать на каждый показатель) → нормализация (`LabAnalyteNormalizer`) →
каскадный поиск в `kb.global_lab_analytes_kb` (`LabAnalyteKbLookupService`).

**Кросс-алфавитное сопоставление (`MedicalTextTransliterator`, `Infrastructure/Search/`).**
`OcrNameCorrector` (триграммное вето на предложенную моделью коррекцию) и `AnalyteSubjectResolver`
(два вето через `IRussianTextSearcher.Score`) отклоняли верные совпадения только потому, что бланк
и модель написали одно и то же понятие разными алфавитами ("Adenovirus"/"аденовирус",
"Hepatitis B virus surface"/"гепатита В вируса поверхность" — 0.27/0.12 в проде, ниже порога 0.3).
`MedicalTextTransliterator.Fold` сворачивает обе стороны сравнения в одну кириллическую форму ПЕРЕД
расчётом схожести (визуальные гомоглифы для одиночных латинских букв типа "Гепатит B", словарь
терминов для нетранслитерируемых слов вроде "surface", иначе — фонетическая транслитерация).
`AnalyteSubjectResolver` дополнительно срезает таксономический хвост у subject ("Rotavirus gr.A" →
"Rotavirus") — это не алфавитный барьер, а AND-семантика `Score`, рубящая совпадение на токенах
хвоста ("gr"/"a"), которых в тексте документа попросту нет.

**Миграция AnalyteKey (план "миграция AnalyteKey", продолжение задачи выше).** `Fold` теперь ещё и
финальный шаг `LabAnalyteNormalizer.NormalizeAnalyteKey` — ПОСТОЯННОГО ключа дедупликации
`LabIndicator.AnalyteKey`/`GlobalLabAnalyteKb.NormalizedName`/`LabAnalyteSearchCache.NormalizedName`
(не путать с обычным `Normalize` без свёртки — тот остаётся ключом для `GlobalSpecimenKb`, у
которого нет аналога бэкофилла). Значит Postgres-поиск (`similarity()` в
`LabAnalyteKbLookupService`) перестал быть кросс-алфавитно слепым для ЛАБОРАТОРНЫХ показателей —
обе стороны сравнения теперь пишутся/ищутся в свёрнутой форме; `kb.global_medications_kb`/
`KbLookupService` (медикаменты, `MedicationNameNormalizer`) свёртку не получили — параллельная
задача, отдельный блочный радиус. Бэкофилл существующих строк — не новый код, а существующий
`LabAnalyteKbRebuildJob` (админка → Enrichment → вкладка rebuild → «Пересобрать», `POST
/api/admin/kb/lab-analytes/rebuild`), который и до этой задачи умел пересчитывать `AnalyteKey`/
`NormalizedName`/`LabAnalyteSearchCache.NormalizedName` "задним числом" после правки нормализации.

Внутри `OcrNameCorrector.RequestCorrectionsAsync` `IsTranslationOf` теперь вызывается на СЫРЫХ
именах, не на `NormalizeAnalyteKey`-форме (та уже без латиницы — гейт молчал бы навсегда) — и
различает "OCR перемешал алфавит ВНУТРИ одного кириллического слова" (`FixMixedScriptHomoglyphs`
чинит это ещё до всякой транслитерации — не перевод, коррекция применяется) от "оригинал реально на
другом языке" (латинское СЛОВО длиннее одной буквы переживает эту починку — перевод, `DisplayName`/
`RawDisplayName` остаётся как в оригинале; сам `AnalyteKey` при этом уже одинаков независимо от
исхода — гейт защищает только отображаемый текст от "мигания" языка между повторными
распознаваниями).

**Честная граница.** Точное равенство ключа после свёртки достижимо, только когда словарный перевод
совпадает по грамматической форме с естественной русской фразой — "Adenovirus"/"аденовирус"
(несклоняемое, оба именительный) сходятся; "Hepatitis B virus surface"/"гепатита В вируса
поверхность" — НЕТ: словарь даёт именительный падеж пословно ("гепатит", "вирус"), а естественная
русская формулировка склоняет их в родительном. Стеммингом это не лечится — у `RussianStemmer`
известный баг ровно на этом слове ("гепатит"→"гепат" как "любит"→"люб", "гепатита"→"гепатит",
РАЗНЫЕ основы). Такая пара всё равно выигрывает от свёртки (похожа по Fold, находит статью KB через
нечёткий `similarity()`), но не делит один `AnalyteKey`/график тренда — см.
`LabAnalyteNormalizerTests.NormalizeAnalyteKey_ProductionLogPair_GrammaticalCaseMismatch_KeysStayDifferent`.

**v2: задача на ЗАПИСЬ, не на вложение.** Кнопка «Распознать» — одна на записи (не на файле),
`POST /api/medical-records/{recordId}/extract` ставит ОДНУ задачу
(`MedicalDocumentExtractionJob`, дедуп-индекс теперь по `MedicalRecordId`), которая
последовательно обрабатывает ВСЕ ещё не распознанные вложения (`FileAttachment.ExtractedAt`
— null у необработанных, проставляется сразу после чтения файла, до финального сохранения —
повтор клика после сбоя не гоняет OCR по уже прочитанным файлам). Показатели из разных файлов
МЕРЖАТСЯ upsert'ом по `(MedicalRecordId, AnalyteKey, SpecimenKbId)`, не blanket-delete — повторный
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

Показатель хранится в `medical.LabIndicators` (`AnalyteKey`/`Flag`/`RefSource`/`SpecimenKbId`
plaintext — по ним поиск/тренд/группировка, значения/референсы `[Encrypted]`). Правится вручную
(`PUT /api/indicators/{id}`, только владелец — исправление ошибок OCR; ref-поля из запроса становятся
новым `RefSource.Blank`, Flag пересчитывается тем же компаратором), добавляется с нуля
(`POST /api/medical-records/{recordId}/indicators` — тот же расчёт `RefSource.Blank`, без повторного
каскада KB) либо удаляется (`DELETE /api/indicators/{id}`) — все три мутации только владелец записи
и все три помечают резюме записи устаревшим (см. «Резюме анализа» ниже).

**Источник анализа (биоматериал) — атрибут ВСЕЙ записи, не показателя.** `MedicalRecord.SpecimenKbId`
— ссылка на общий справочник `kb.global_specimens_kb` (`GlobalSpecimenKb`; сентинел
`SpecimenContextIds.Unresolved`, пока источник не определён), денормализована на каждый
`LabIndicator.SpecimenKbId` (инвариант: всегда равен полю записи). Определяется `SpecimenResolver`
при распознавании либо вручную `PUT /api/medical-records/{id}/specimen` (каскад на все показатели,
сбрасывает `SpecimenHint`, ставит пересчёт резюме); в UI поле «Биоматериал» — в форме
«Редактировать запись». Если модель увидела обобщённое слово без локализации («мазок») — оно
сохраняется как `MedicalRecord.SpecimenHint`, запись остаётся Unresolved, а карточка просит уточнить.
Ключ группировки истории/тренда — `(пациент, AnalyteKey, SpecimenKbId)`, где пациент =
`(FamilyDependentId, TargetUserId)` (`GetMyIndicatorsAsync`): без источника лейкоциты крови и мочи
слились бы на одном графике, без пациента — показатели разных членов семьи.

Ввод источника: `GET /api/specimens/search` (нечёткий поиск pg_trgm по справочнику, без LLM),
`GET /api/specimens` (недавно использованные — `UserSpecimen` хранит только `SpecimenKbId` +
`LastUsedAt`), `POST /api/specimens` (`UserSpecimenService.CreateAsync`: рамки по длине/символам до
вызова модели → поиск в общем справочнике → для НОВОГО названия один вызов `ILmStudioJsonClient` и
**детерминированное вето поверх ответа модели**, тот же приём, что
`MedicationEnrichmentProcessor.ResolveCorrectedName`: `TrigramSimilarity` введённого и предложенного
имени `< 0.3` → отклонить). LM Studio недоступен → `503`, не «принять на веру»; фронт в этом
случае не теряет ввод, а отправляет его на `PUT .../specimen-pending` (`PendingSpecimenText`), и sweep
применяет источник, когда ИИ вернётся ([ADR-0013](../../docs/adr/0013-llm-unavailability-waiting-queue.md)).

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

**Резюме анализа.** `MedicalRecord.SummaryJson` (`[Encrypted]`) строит `LabSummarizer` (один вызов
LLM + антигаллюцинационный гейт) — при распознавании и после ручных правок. Кнопки «Пересчитать» нет:
создание/правка/удаление показателя и смена источника вызывают
`ExtractionQueryService.MarkSummaryDirtyAsync` → `RecordSummaryRegenerationJob.RescheduleAsync`:
ставится токен-версия `MedicalRecord.SummaryDirtyAt` (микросекундная точность, как у Postgres) и
планируется джоба очереди `extraction` с задержкой 15 с (дебаунс серии правок). Джоба выходит, если
токен уже другой (свежая правка поставила свою джобу), и сверяет его ещё раз ПОСЛЕ вызова LLM.
`GET .../summary` отдаёт `pending: true`, пока `SummaryDirtyAt` не пуст (при этом старый текст
остаётся виден; если резюме ещё нет — 200 с пустыми полями и `pending: true`, а не 404). `POST
.../summary/regenerate` больше не вызывает LLM синхронно: ставит ту же джобу без задержки и отвечает
`202` + `pending` (нужен, когда резюме ещё нет; только владелец). Смысловой отказ (гейт/невалидный
JSON) сбрасывает резюме и пометку; недоступность ИИ — оставляет пометку.

**Недоступный ИИ («ждём ИИ»).** Сводка — [ADR-0013](../../docs/adr/0013-llm-unavailability-waiting-queue.md)
и [`ai-availability-and-waiting-queue.md`](ai-availability-and-waiting-queue.md). Кратко по коду
модуля: (1) `ExtractionRequestService.RequestAsync` при недоступном ИИ создаёт задачу с
`WaitingForAi=true` БЕЗ Hangfire-энкью и возвращает `QueuedWaitingForAi` (`202
{code: waiting_for_ai}`); (2) `MedicalDocumentExtractionProcessor` перехватывает
`LmStudioUnavailableException` отдельным `catch`: задача → `Pending`, `WaitingForAi=true`, попытка
`[AutomaticRetry]` не тратится, ничего не пробрасывается (прочие исключения идут прежним путём с
3 попытками и терминальным `Failed`); (3) `ExtractionStatusResponse.WaitingForAi` и
`IndicatorDto.EnrichmentWaitingForAi` — признаки для UI; (4) `CheckManualEntryGatesAsync`:
`IsTransientFailure` гейта легитимности/правдоподобия НЕ блокирует постановку обогащения (процессор
обогащения повторит гейты сам); суммаризаторы обогащения (`LabAnalyteKbSummarizer`,
`MedicationSummarizer`) при `IsTransient` возвращают `EnrichmentFailureReason.LmStudioUnavailable`, и
процессоры (лаб-показатели, препараты, препараты из заключений) помечают задачу `IsTransientFailure`
— sweep такие задачи подхватывает; (5) `LmStudioRecoverySweepJob` — раз в минуту, если проба
`ILmStudioAvailabilityProbe` проходит: запускает ждущие распознавания, перепланирует резюме со
`SummaryDirtyAt` старше 2 минут, проверяет `PendingSpecimenText` (валидно → `SetRecordSpecimenAsync`,
отказ → `SpecimenHint`), затем прежний досып `Failed`+`IsTransientFailure` (четыре конвейера, окно
7 дней). Известные пробелы — в `TECH_DEBT.md`.

**Удаление.** `MedicalRecordService.DeleteAsync` (а также удаление подопечного и аккаунта) явно
удаляет `MedicalDocumentExtractionJobs` записи — у задачи нет FK на запись; без этого «ждущая»
задача висела бы сиротой в глобальном трее (`UserJobsService` дополнительно игнорирует задачи
несуществующих записей). Задачи обогащения справочника не удаляются: справочник общий.

Конвейер обогащения `kb.global_lab_analytes_kb` — зеркало `MedicationEnrichmentProcessor`:
`IMedicationSearchProvider.SearchAsync(name, WebSearchTopic.LabAnalyte)` (отдельный список
доверенных доменов — `EnrichmentOptions.AnalyteTrustedDomains`: helix.ru/invitro.ru/gemotest.ru/
kdlmed.ru/cmd-online.ru) → `LabAnalyteKbSummarizer` (тот же антигаллюцинационный гейт; v2 —
промпт также просит `sex` на каждый диапазон и `calculationInstructions`) → `LabAnalyteKbWriter`
(upsert, `KbIsolationGuard`). Месячной квоты больше нет (ADR-0005 §9, третья редакция) — вместо
неё ручной вентиль `IWebSearchValveService` (одна строка `WebSearchConfig`, БЕЗ кеша — закрытие
должно останавливать платные вызовы немедленно), общий на все три enrich-конвейера. Закрытие не
отменяет задачу, а переводит в `EnrichmentJobStatus.Deferred`; `DeferredEnrichmentReleaseJob`
возобновляет все такие задачи при открытии. См. дополнение к
[ADR-0005](../../docs/adr/0005-medication-enrichment-egress.md).

**Аудит платных вызовов веб-поиска (`WebSearchCallLog`, схема `public`).** До этого одна строка
на КАЖДЫЙ вызов `IMedicationSearchProvider.SearchAsync` (оба провайдера, все три enrich-процесса —
`LabAnalyteEnrichmentProcessor`/`MedicationEnrichmentProcessor`/`VisitMedicationEnrichmentProcessor`,
включая кэш-хиты, `WebSearchCallOutcome.CacheHit`) восстановить "сколько заплачено и за что" было
нельзя — только `job.ExternalSearchAt`/`Provider` и перезаписываемая строка кэша. Пишет
`WebSearchCallLogger` (`FamilyHub.Infrastructure.Enrichment`) — из **собственного** `IServiceScopeFactory`-
скоупа, не из скоупного `AppDbContext` вызывающего процессора (там уже лежат незакоммиченные
изменения задачи, ранний `SaveChangesAsync` закоммитил бы их раньше времени); ошибка записи лога
только логируется, не роняет сам поиск. Хранит `QueryText` целиком (не ПДн — тот же нормализованный
запрос, что уходит наружу, ADR-0005 §1) и URL реально использованных источников. Ретеншн — 180 дней,
в уже существующем `AuditRetentionJob` (не отдельная джоба). Админка — страница «Операции → Журнал вызовов»
(`/admin/operations/search-calls`; `AdminSearchCallsEndpoints`, `/api/admin/search-calls*`): список с
фильтрами, полная карточка вызова, `/stats` — доля кэш-хитов, разбивка по провайдеру/исходу, расход
текущего месяца (плюс денежная оценка, если задана `Enrichment:PricePerPaidCall`), состояние
вентиля.

**Прогрев кэша веб-поиска из админки (`SearchWarmupRun`, схема `public`).** Проактивно наполняет
`kb.medication_search_cache`/`kb.lab_analyte_search_cache` по вставленному в textarea списку
названий — на каждое имя делает ТОЛЬКО `provider.SearchAsync` → `*SearchCacheService.RecordSearchAsync`,
БЕЗ единого обращения к локальной LLM (ни `LegitimacyGuardService`, ни суммаризация, ни запись в
`kb.global_*_kb`) — придумано ради грантового лимита облачного провайдера поиска, который сгорает
по времени, а обычная задача обогащения тратит LLM дважды на единственном воркере очереди
`enrichment` и не успела бы потратить лимит до его истечения. Справочник наполняется позже
бесплатно — обычный конвейер обогащения найдёт уже свежую строку кэша на живом пользовательском
спросе. `WarmupNameParser.Parse` — та же нормализация, что и сам конвейер (`MedicationNameNormalizer.Normalize`/
`LabAnalyteNormalizer.NormalizeAnalyteKey`), дедуп по нормализованному ключу до вызова провайдера.
`SearchCacheWarmupJob` — батч 10 + самопродолжение (тот же приём, что `LabAnalyteKbReenrichJob`),
резюмируемый курсор в строке прогона (зеркало `KbRebuildRun`); имена, уже попавшие в справочник или
свежий кэш, пропускаются молча. Бюджет платных вызовов на прогон — `SearchWarmupRun.MaxPaidCalls`
(null = без ограничения). Вентиль (см. выше) прогон не отменяет, а паркует —
`SearchWarmupStatus.Paused`, курсор остаётся, `DeferredEnrichmentReleaseJob` возобновляет
энкью на `SearchCacheWarmupJob.RunAsync` при открытии. Немедленная безусловная остановка из
админки (`AdminSearchWarmupService.CancelAsync`) пишет терминальный статус напрямую в БД, не
дожидаясь, пока фоновая джоба сама заметит флаг — иначе падение процесса сервера посреди прогона
оставляло бы строку в `Running` навсегда, блокируя новый прогон уникальным индексом. Админка —
страница «Операции → Прогрев» (`/admin/operations/warmup`; `AdminWarmupEndpoints`, `/api/admin/enrichment/warmup*`).

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

**Батч-загрузка и автоопределение вида (`KindIsAutoDetected`).** Кнопка «Несколько» (топбар,
рядом с «+ Добавить», `PageActionService.secondaryAction`) открывает `record-batch-add.component.ts`
(`/health/records/batch`, `/health/visits/batch`) — за одну операцию N документов ОДНОГО пациента,
каждый становится СВОЕЙ записью со своим прогоном пайплайна (не мержится в один набор, в отличие
от многостраничного бланка через «+ Добавить»). Фронт просто шлёт N раз существующую тройку
`POST /api/medical-records` (с `AutoDetectKind: true`) → `POST .../attachments` → `POST .../extract`
последовательно — новых batch-эндпоинтов нет. Вид записи не спрашивается: `MedicalRecord.Kind`
создаётся провизорным (по вкладке, откуда загружали) с флагом `KindIsAutoDetected=true`;
`DocumentKindClassifier` (`Extraction/`, один LLM-вызов на документ, тем же приёмом, что
`SpecimenResolver`/`AnalyteSubjectResolver`) определяет фактический вид ДО выбора системного
промпта `analysis.extract`/`visit.extract` внутри `LmStudioMedicalDocumentExtractor.ExtractAsync`
(параметр `kind` стал `MedicalRecordKind?` — `null` значит «определи сам»); `ExtractionResult.Kind`
несёт фактически применённый вид. `MedicalDocumentExtractionProcessor.RunAsync` после цикла по
файлам считает итог большинством голосов по `results[].Kind` (ничья/не уверен → `Analysis`),
переставляет `record.Kind` и снимает флаг — повторный клик «Распознать» больше не переопределяет
уже решённый вид. Необязательный шаг пайплайна `kind-classify` (`AnalysisExtraction`, промпт
`document.kind-classify`) — выключен из админки означает "остаться на Analysis". Известная
UX-складка: запись, распознанная не тем видом, чем провизорный, переезжает в другую вкладку после
`Completed` — бэйдж «Вид определяется автоматически» на карточке (`item.kindIsAutoDetected`) плюс
существующий push смягчают, отдельного "переноса на лету" нет.

**Лимиты пайплайна на пользователя (`ExtractionLimitsOptions`, секция `ExtractionLimits`, не
`Extraction` — та занята `Infrastructure.Documents.ExtractionOptions`).** До этого один пользователь
мог держать сколько угодно параллельных задач `extraction` (дедуп — только по `MedicalRecordId`) и
надолго занять единственный воркер LM Studio за WireGuard (`LmStudioConcurrencyGate`). Три
независимых барьера: `MaxBatchDocuments` (мягкий, фронт не даёт застейджить больше — реальная защита
ниже), `MaxActiveJobsPerUser` (`ExtractionRequestService.RequestAsync` считает
`Pending`/`Running`-задачи по `RequestedByUserId` — мягкий лимит, гонка возможна, это осознанно) и
`DailyJobsPerUser` (считает `MedicalDocumentExtractionJobs.CreatedAt` за текущие сутки UTC — в
Postgres, переживает рестарт). Оба новых исхода `ExtractionRequestResult` (`TooManyActiveJobs`/
`DailyQuotaExceeded`) отдаются как `429` с `{code, message}`. `GET /api/medical-records/extraction-limits`
(`ExtractionRequestService.GetLimitsAsync`) — снимок лимитов + текущего расхода, тот же приём, что
`GET /api/attachments/limits`. Rate limiting (`Program.cs`, `AddRateLimiter`) получил две новые
политики, партиция по `UserId` (не по IP, в отличие от `auth`/`auth-code`/`invite-redeem`) —
`"llm"` (`POST .../extract`, `.../summary/regenerate`, `POST /api/medications/ocr`) и
`"medical-write"` (`POST /api/medical-records`, `POST .../attachments`).

Фронт (`FamilyHub.Web`, актуальное состояние после редизайнов v2–v2.3; хронология — в
[`ui-rework-v2.3.md`](ui-rework-v2.3.md)): вкладка «Анализы» (`medical-records-panel`) — записи
**сгруппированы по человеку** (аватар, «N анализов · последний …», доступ словами) с таймлайном по
датам, бесконечная прокрутка (`pageSize` 50, не пагинация), фильтр по людям чипами; поиск — в
топбаре каркаса (`PageActionService.pageSearch`), «Фильтры» (период/врач) — попап/шторка. «Открыть» ведёт
на отдельную страницу записи `/health/records/:id` (`RecordDetailPageComponent`; та же панель в
single-mode обслуживает и страницу визита врача). Форма создания — отдельные роуты
(`record-add`, `record-batch-add`), пациент — из self/подопечный/участник, врач — нативный
`<datalist>` (typeahead-компонента в проекте нет, осознанно). Одна кнопка «Распознать» на записи, живой
прогресс — `shared/pipeline-progress` (шаги с галочками; при недоступном ИИ — шаг «Ждём ИИ»,
опрос статуса реже). Открытая запись: шапка «‹ Анализы» + действия (Файлы/Резюме/Редактировать/
Доступ/Удалить), мета-строка, плитки «вне нормы/в норме/без нормы», таблица показателей со шкалой
`shared/reference-scale` (подписи границ нормы и значения), клик по показателю — справка
(`components/indicator-info`: боковая панель на десктопе, полноэкранно на мобильном). Вкладка
«Показатели» (`indicators-tab`, `/health/indicators`) группирует по `(пациент, analyteKey,
specimenKbId)`; подписи источника — `shared/util/specimen.ts`.

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

### Превью вложений (встроенный вьюер, `Attachments/AttachmentPreviewProcessor.cs`)

Модель артефактов — три вида (`AttachmentPreviewKind`: `Thumbnail`/`Pdf`/`Page`), не «страница
на каждый лист документа»: PDF получает только `Thumbnail` (вьюер рисует оригинал сам, pdf.js);
Office (`docx`/`xlsx`/`xls`/`doc`/`rtf`/`html`) конвертируется в PDF через сайдкар **Gotenberg**
(LibreOffice headless, `Previews.GotenbergConverter`/`IGotenbergConverter` — у NPOI нет движка
вёрстки, только извлечение текста) и получает оба артефакта, `Pdf` + `Thumbnail` из его первой
страницы; HEIC/TIFF получают нормализованный `Page` (то, что реально может показать `<img>`,
браузер их не умеет) плюс `Thumbnail`; jpeg/png/webp — только `Thumbnail`, вьюер показывает
оригинал напрямую. text/csv/xml — без артефактов вовсе, вьюер тянет исходник инлайном.

Диспетчер — `Infrastructure.Previews.AttachmentPreviewRenderer`, переиспользует конвейер OCR
(`PdfPageRasterizer`, `ImageDownscaler`), не собственный код рендера. `FileAttachment.PreviewStatus`
(`None|Pending|Ready|Failed|Unsupported`) — и дедуп очереди, и то, что видит клиент. Генерация —
Hangfire, очередь `previews` (`WorkerCount=2`, CPU-bound, не делит ограничение LM Studio/внешнего
поиска с `extraction`/`enrichment`), энкью — best-effort сразу после `UploadForMedicalRecordAsync`
(в отличие от `ExtractionRequestService`, сбой энкью НЕ откатывает сам факт загрузки файла —
превью произвольный, а не основной артефакт); легаси-вложения без превью (`PreviewStatus=None`,
загружены до этой функции) получают его лениво — при первом `GET /preview`.

Превью — **регенерируемый кэш**: `EncryptionRotationJob` их не перешифровывает, а удаляет
(`ResetStalePreviewsAsync`, не резюмируемый курсор, в отличие от фазы 2) — пересоздаются по
требованию. Исключены из экспорта аккаунта (`AccountService`) — производные данные, не
пользовательский контент.

Раздача байт — тот же принцип подписанных ссылок, что у `/file`, но с добавленным **scope**
(`DownloadTokenService.DownloadScope`: `File`/`Inline`/`Thumbnail`/`PreviewPdf`/`PreviewPage`) —
payload подписи включает scope, поэтому ссылка на миниатюру не открывает оригинал и наоборот.
`/inline` и `/preview/*` отдают `Results.Stream` **без** `fileName` (иначе
`Content-Disposition: attachment` не даст браузеру/pdf.js отрисовать вместо скачивания).

Маршруты: `GET /api/attachments/{id}/preview` (авторизованный, отдаёт `AttachmentPreviewDto` —
уже готовые подписанные ссылки + `AttachmentRenderKind`), `GET /api/attachments/{id}/inline`,
`GET /api/attachments/{id}/preview/{thumb|pdf|page}` (все — `AllowAnonymous` + HMAC, как `/file`).

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
  `Pending`/`Running`/`Deferred` задач (последний — вентиль закрыт, ADR-0005 §9, задача жива).

  **Дедуп дублирующихся Failed-задач (найдено на проде — «Требует внимания» заполнялся десятками
  одинаковых карточек).** Частичный индекс выше дедупит только ПОКА задача жива — Failed из-под
  него выпадает, а Failed-задача никогда не пишет в KB (`Hit` так и не появится), значит любое
  повторное срабатывание (`UpdateAsync` вызывает `RequestAsync` безусловно на КАЖДОЕ сохранение,
  даже правку `ExpiryDate` без изменения имени; извлечение документа переспрашивает каждый
  непризнанный показатель/препарат на КАЖДЫЙ прогон — `MedicalDocumentExtractionProcessor.
  ProcessAnalysisAsync`/`ProcessVisitAsync`) заводило новую Failed-задачу с той же причиной
  бесконечно. Все три `*RequestService.RequestAsync` (медикаменты, показатели, медикаменты из
  заключений — `EnrichmentRequestService`/`LabAnalyteEnrichmentRequestService`/
  `VisitMedicationEnrichmentRequestService`) теперь дополнительно проверяют «уже проваливалось
  ранее» (`Status IN (Failed, Skipped)` по тому же ключу) ПЕРЕД вставкой и молча выходят, если да —
  без изменений извне (новый доверенный домен, правка промпта) повторная автоматическая попытка
  дала бы тот же результат, а ручной путь (админка → «Требует внимания» → «Перезапустить»/«Доверить
  и перезапустить») работает с уже существующей строкой и этой проверке не подчиняется. `force:
  true` (`LabAnalyteKbReenrichJob`/ручной reenrich из админки) намеренно проходит мимо — там цель
  ИМЕННО повторить попытку. Задним числом уже накопленные дубли чистит
  `POST /api/admin/pipeline/jobs/dedupe-failed` — группирует Failed/Skipped по (название[,
  биоматериал]), оставляет только самую свежую строку в каждой группе. `AdminAttentionService`
  теперь считает `DistinctCount` (сколько РАЗНЫХ названий) рядом с сырым `Count` — расхождение
  подсвечивает накопившиеся дубли даже после фикса (на старых базах).
- **`MedicationEnrichmentProcessor`** — Hangfire-джоба в выделенной очереди `enrichment`
  (`[Queue("enrichment")]`, один воркер — см. `Program.cs`, естественно укладывается в лимит
  Brave free-tier 1 req/s), `[AutomaticRetry(Attempts = 3)]` только на настоящие сбои; ожидаемые
  исходы (нет доверенных источников) переводят статус задачи в `Failed` обычным `return`, без
  ретрая; закрытый вентиль — в `Deferred` (не терминально, возобновляется автоматически).
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
