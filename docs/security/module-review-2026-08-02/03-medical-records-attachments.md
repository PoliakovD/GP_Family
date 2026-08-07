# Модуль: Мед-анализы (персональный ресурс) и вложения

**Файлы:** `Modules.Medical/MedicalRecords/*`, `Modules.Medical/Attachments/*`,
`Infrastructure/Storage/*` (в части использования из этого модуля)

**Статус:** 🟡 2/3 закрыты (находки 1, 2 — см. пометки ✅; №3, TOCTOU-окно подписанных ссылок —
уже задокументировано в коде как осознанное решение, действий не требовалось).

## Сводка

Самый чувствительный с точки зрения ПДн модуль (медицинские данные) — и сделан аккуратнее всего:
инвариант «шарингом/скрытием управляет только владелец» выдержан жёстко и на уровне сервиса, и на
уровне комментариев/тестов (упоминается `KbIsolationGuardTests` в соседнем модуле — тот же уровень
строгости). Явных дыр в контроле доступа не найдено. Находки ниже — по периферии (обработка имён
файлов, отсутствие явных лимитов на загрузку, окно TOCTOU у подписанных ссылок).

## 🟡 Средний приоритет

### 1. Zip Slip: имя файла вложения не санитизируется и используется как часть пути записи в ZIP-экспорте

> ✅ **Исправлено на обоих уровнях, как рекомендовано.** Новый `FileNameSanitizer`
> (`Infrastructure/Storage/FileNameSanitizer.cs`) — отсекает сегменты пути по ОБОИМ разделителям
> (`/` и `\`, независимо от ОС сервера), управляющие символы (в т.ч. null-байт), ведущие/замыкающие
> точки; применяется в двух местах: (1) `AttachmentService.UploadForMedicalRecordAsync` —
> санитизирует ДО записи в БД (первичный фикс, закрывает проблему у источника); (2)
> `AccountService.BuildZipAsync` — санитизирует повторно прямо перед построением имени ZIP-записи
> (defense in depth, покрывает и гипотетические старые строки). Покрыто параметризованным тестом
> `AttachmentsApiTests.Upload_WithPathTraversalFileName_IsSanitized` (`../../../etc/passwd`,
> `..\..\windows\system32\evil.pdf`, `/etc/passwd`, `..`).

- **Где:**
  - `Modules.Medical/Attachments/AttachmentEndpoints.cs:13-18` — `FileName` берётся как есть из
    `IFormFile.FileName` (клиентский `Content-Disposition`, ASP.NET Core его не санитизирует).
  - `Api/Features/Account/AccountService.cs:193` —
    `zip.CreateEntry($"attachments/{row.OwnerId}/{download.Value.FileName}");`
- **Проблема:** ASP.NET Core не проверяет `IFormFile.FileName` на предмет `../`, абсолютных путей
  или разделителей каталогов — это значение от клиента, который теоретически может его
  подделать (не через штатный веб-фронт, а через прямой вызов API). Оно сохраняется в
  `FileAttachment.FileName` как есть и позже используется как сегмент имени файла в ZIP-архиве
  персонального экспорта (`/api/account/export`). Если имя содержит `../` или похожие
  последовательности, при распаковке архива инструментом, не защищённым от classic Zip Slip
  (не все инструменты одинаково защищены — .NET-овский `ZipFile.ExtractToDirectory` защищён с
  .NET Core 3.0+, но пользователь может открыть архив чем угодно), запись может попытаться выйти
  за пределы целевой папки.
- **Почему это важно:** эксплуатация ограничена — это собственный экспорт того же пользователя
  (не кросс-юзерная атака), поэтому практический ущерб низкий (пользователь «атакует» свой же
  компьютер собственным файлом, который сам туда и положил, либо файл был загружен через какой-то
  альтернативный клиент бота/API с нестандартным именем). Тем не менее это классический паттерн,
  который стоит закрыть системно: путь на диске уже безопасен (используется `attachmentId`, не
  имя файла — см. `AttachmentService.cs:51`), но конкретно ZIP-экспорт — нет.
- **Рекомендация:** санитизировать `FileName` на входе (запрет `..`, `/`, `\`, null-байтов) в
  `AttachmentEndpoints`/`AttachmentService.UploadForMedicalRecordAsync`, и/или дополнительно
  экранировать перед построением имени ZIP-записи в `AccountService.BuildZipAsync`
  (`Path.GetFileName(...)` как минимум).

### 2. Нет явного allow-list по content-type и явного лимита размера на загрузку вложений

> ✅ **Исправлено.** `AttachmentService.AllowedContentTypes` — allow-list (image/jpeg, image/png,
> image/webp, image/heic, application/pdf), проверяется до какой-либо работы с файлом → `415`
> при отказе. `AttachmentService.MaxSizeBytes = 30 МиБ` (явный лимит, выбран как разумный для
> сканов мед-документов) → `413` с телом `{code, maxSizeBytes}`. Kestrel `MaxRequestBodySize`
> явно поднят до 40 МиБ в `Program.cs` — иначе implicit-дефолт (~28.6 МиБ) обрубал бы запрос
> раньше, чем срабатывала бы наша проверка с понятным ответом. Покрыто тестами
> `AttachmentsApiTests.Upload_WithDisallowedContentType_Returns415` и
> `Upload_OverSizeLimit_Returns413`.

- **Где:** `Modules.Medical/Attachments/AttachmentService.cs:32-82`
  (`UploadForMedicalRecordAsync`) — принимает произвольный `contentType`, размер ограничен только
  дефолтными лимитами Kestrel/ASP.NET Core (не переопределены явно в найденном коде).
- **Смягчающий фактор:** при скачивании `AttachmentEndpoints.cs:43-54` использует
  `Results.Stream(content, contentType, fileName)` — передача `fileDownloadName` заставляет
  ASP.NET Core проставить `Content-Disposition: attachment`, что вынуждает браузер СКАЧИВАТЬ файл,
  а не рендерить inline — это существенно снижает риск stored-XSS через загруженный
  `text/html`/`image/svg+xml` с полезной нагрузкой, даже если `ContentType` полностью
  контролируется атакующим при загрузке.
- **Рекомендация:** для defense-in-depth стоит явно ограничить допустимые `ContentType`
  (изображения/PDF) и/или явно задать `RequestSizeLimit`/`MultipartBodyLengthLimit` на эндпоинте
  загрузки, а не полагаться на дефолты фреймворка.

### 3. TOCTOU-окно у подписанных ссылок на скачивание (до 5 минут)

- **Где:** `Infrastructure/Storage/DownloadTokenService.cs` (`AttachmentDownloadOptions.UrlTtl`,
  дефолт 5 минут), `Modules.Medical/Attachments/AttachmentService.cs:88-127` (`GetPresignedUrlAsync`
  — авторизация проверяется здесь, в момент выдачи ссылки), `AttachmentEndpoints.cs:43-54`
  (сам download-эндпоинт — `AllowAnonymous`, проверяет только HMAC-подпись и срок).
- Задокументировано в коде как осознанное решение («Авторизация происходит в момент ВЫДАЧИ
  ссылки»). Если доступ отозван (владелец снял шаринг/скрыл запись от семьи) ПОСЛЕ выдачи ссылки,
  но ДО истечения её TTL — уже выданная ссылка продолжит работать до 5 минут. Окно небольшое и
  сознательно принятое — фиксируется здесь просто для полноты картины (в духе «на всякий случай
  выписать, чтобы не упустить»), не как призыв к обязательному действию.

## 🟢 Низкий приоритет / на заметку

- ~~`AttachmentEndpoints.MapAttachmentEndpoints` вызывает `.DisableAntiforgery()` на upload-роуте —
  на текущий момент это no-op...~~ **Устарело.** После фикса [01-auth-identity.md, находка 4]
  (добавлен `AddAntiforgery()`) `.DisableAntiforgery()` на upload-эндпоинтах (Attachments + OCR)
  снова осмыслен: без него запрос падал бы в 500 (ASP.NET Core автоматически требует
  антифорджери-валидацию для любого `IFormFile`-эндпоинта, а `app.UseAntiforgery()` намеренно не
  подключён — CSRF проверяется собственным глобальным гейтом в `Program.cs`). Реальная
  CSRF-защита для этих эндпоинтов идёт через тот гейт, не через встроенный механизм.
- `TECH_DEBT.md` уже фиксирует отсутствие `GET /api/medical-records/{id}/attachments` (список
  вложений записи) — не новая находка, но подтверждена: `MedicalRecordEndpoints.cs` и
  `AttachmentEndpoints.cs` действительно не содержат такого маршрута.

## ✅ Проверено, проблем не найдено

- Инвариант «шарингом/скрытием управляет только владелец, даже админ семьи не может» — жёстко
  выдержан в `MedicalRecordService.ShareWithFamilyAsync/UnshareFamilyAsync/HideFromFamiliesAsync/
  UnhideFromFamiliesAsync` (явная проверка `record.OwnerUserId != ownerUserId` → `Forbidden`).
- `VisibleRecordsQuery` (главный запрос видимости, раздел 6 брифа) — корректно комбинирует
  владение + активное членство в семье, которой расшарено + отсутствие точечного скрытия именно от
  этой семьи; при чтении чужих (расшаренных) записей пишется аудит (`MedicalAccessAction.ViewList`).
- Вложения шифруются at-rest AES-256-GCM целиком блобом (`AesGcmFileCipher`) до записи в
  хранилище; storage key строится из `attachmentId`, не из имени файла — ФИО/диагноз не попадают
  ни в путь на диске/в бакете, ни в логи (`logger.LogDebug` логирует только идентификаторы).
- Доступ к вложению медикамента (`FileOwnerType.Medication`) корректно проверяется через
  `IFamilyAccessService.HasRoleAsync` по `FamilyId` медикамента — не через владельца записи.
- Аудит доступа (`MedicalAccessAction.DownloadAttachment`) пишется синхронно в момент авторизации
  (выдачи ссылки), не позже.
