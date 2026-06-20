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

## Анализы (`MedicalRecords/`) — персональный ресурс с двухуровневым шарингом

`MedicalRecordService` — **не** использует `IFamilyOwned`/`IFamilyAccessService.HasRoleAsync`
для проверки видимости самой записи (только для проверки «состоишь ли ты в семье, которой
шаришь» при `ShareWithFamilyAsync`). Видимость считается явным предикатом
`VisibleRecordsQuery(userId)`:

```
видно, если: ты владелец
           ИЛИ (твои анализы расшарены этой семье
                И ты в ней активный член
                И эта конкретная запись не скрыта именно от неё)
```

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
(Local/MinIO, см. `infrastructure.md`). У вложения **нет собственной видимости** — она
наследуется от родителя через `OwnerType`+`OwnerId`:

- `OwnerType.MedicalRecord` → видимость через `MedicalRecordService.IsVisibleToAsync` (та же
  логика двухуровневого шаринга).
- `OwnerType.Medication` → видимость через `IFamilyAccessService.HasRoleAsync` на `FamilyId`
  лекарства (`HasMedicationAccessAsync`).

Загружать вложение к анализу может только владелец записи (`UploadForMedicalRecordAsync`) —
тот же барьер, что и для шаринга. Скачивание — только presigned URL с TTL 5 минут
(`GetPresignedUrlAsync`), не прямая ссылка.

Маршруты: `POST /api/medical-records/{recordId}/attachments` (multipart `file`),
`GET /api/attachments/{attachmentId}/url` → `{ url }`.

**Известное ограничение v1**: нет эндпоинта «список вложений записи» — см.
`TECH_DEBT.md` п.1. Если будете добавлять, естественное место — новый метод в
`AttachmentService` (`GetForMedicalRecordAsync(recordId, userId)`, с той же проверкой
видимости через `MedicalRecordService.IsVisibleToAsync`, что и у `GetPresignedUrlAsync`).

## Wiring модуля (`MedicalModule.cs`)

`AddMedicalModule()` регистрирует `MedicationService`, `MedicalRecordService`,
`AttachmentService` в DI; `MapMedicalModule()` вызывает три `Map*Endpoints()`. Подключается из
`FamilyHub.Api/Program.cs`, не имеет обратной зависимости на `FamilyHub.Api` или
`FamilyHub.Modules.Birthdays`.
