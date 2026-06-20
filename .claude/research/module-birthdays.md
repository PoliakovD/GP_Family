# `FamilyHub.Modules.Birthdays`

Самый простой модуль в проекте — хороший шаблон для любого нового семейного ресурса.

`BirthdayService` — CRUD по тому же образцу, что и `MedicationService`
(`module-medical.md`): `Birthday` реализует `IFamilyOwned`, видим всем активным членам семьи
(`FamilyRole.Member` и выше может и читать, и писать — здесь нет разделения ролей внутри
семьи, в отличие от инвайтов/выгона, где нужен `Admin`). `Update`/`Delete` сначала грузят
запись по `Id`, затем проверяют доступ к её реальному `FamilyId` — не доверяют `familyId` из URL.

Единственное поле кроме имени — `Date` (`DateOnly`, без года ограничений: год хранится, но
бизнес-логика повторения ежегодна, см. `ReminderScanJob.NextOccurrence`/`SafeDate` в
`infrastructure.md` — 29 февраля в невисокосный год переносится на 28-е, а не падает).

Маршруты: `GET/POST /api/families/{familyId}/birthdays`, `PUT/DELETE /api/birthdays/{birthdayId}`.

## Wiring модуля (`BirthdayModule.cs`)

`AddBirthdayModule()` регистрирует `BirthdayService`; `MapBirthdayModule()` вызывает
`MapBirthdayEndpoints()`. Подключается из `FamilyHub.Api/Program.cs`, не зависит от
`FamilyHub.Modules.Medical` и не зависит от него.

## Если нужен ещё один такой же простой семейный модуль

Скопировать структуру 1:1: `XModule.cs` (`AddXModule`/`MapXModule`), `XService.cs`
(конструктор `(AppDbContext db, IFamilyAccessService access)`, каждый метод начинается с
`access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct)`), `XDtos.cs` (record-типы
запросов/ответов), `XEndpoints.cs` (`app.MapGroup("/api")`, без отдельной авторизационной
политики — `FallbackPolicy` уже требует аутентификацию).
