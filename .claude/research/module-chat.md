# FamilyHub.Modules.Chat — чат семьи

Решение и мотивы — [ADR-0019](../../docs/adr/0019-family-chat.md); упрощения v1 — `TECH_DEBT.md` #20.
Здесь — как устроено и где грабли.

## Карта

| Что | Где |
|---|---|
| Сущности | `Domain/Entities/ChatMessage*.cs`, `ChatReadState.cs`, `Enums/ChatLinkKind.cs`; схема БД `chat` |
| REST | `Modules.Chat/Chat/ChatEndpoints.cs` → `ChatService` (`/api/families/{id}/chat/...`, `/api/chat/{unread,config,hub-ticket}`) |
| Хаб | `Modules.Chat/Realtime/ChatHub.cs` (`/hubs/chat`), `ChatEvents` (рассылка), `ChatPresenceTracker` |
| Вход в хаб | `Infrastructure/Auth/HubTicketService.cs` + `HubTicketAuthenticationHandler.cs`, ветка `/hubs` в `AuthenticationRegistration` (Smart-селектор) |
| Уведомления | `ChatMessageNotifyJob` (Hangfire), `ChatPushCustomizer`, `NotificationType.FamilyChatMessage` |
| Ссылки на сущности | `Infrastructure/Chat/IChatLinkResolver.cs` → `Modules.Medical/Chat/MedicalChatLinkResolver.cs` |
| Вложения | `ChatAttachmentStore` (запись), просмотр — существующие `/api/attachments/{id}/...` (ветка `FileOwnerType.ChatMessage` в `AttachmentService.HasAccessAsync`) |
| Чистка | `FamilyService.DeleteFamilyAsync`, `AccountService.DeleteAccountAsync` (+ `chat-messages.json` в экспорте) |
| Фронт | `services/chat-realtime.service.ts` (одно соединение на приложение), `components/family-chat/*`, вкладка `?tab=chat` в `family-details` |

## Поток сообщения

1. `POST .../chat/messages` (multipart: `text`, `replyToMessageId`, `links` — JSON, `files`) → `ChatService.SendAsync`:
   проверка членства, ответа (та же семья), ссылок (автор должен их **видеть**), лимитов; блобы шифруются и
   кладутся **до** коммита, при сбое коммита подчищаются; автору сразу ставится отметка прочтения.
2. После коммита — `IChatEvents.MessageChangedAsync` (группа `family:{id:N}`, payload только id) и
   `Enqueue<ChatMessageNotifyJob>`.
3. Клиенты получают `messageChanged` → `GET .../messages/{id}` → карточки ссылок резолвятся под зрителя.
4. Задача уведомлений: активные члены − автор − те, кто `Viewing` этот чат; DedupKey
   `chat:{familyId:N}:{userId:N}:{CreatedAt.Ticks / окно}` → не чаще раза в 10 минут.

## Грабли и неочевидное

- **Тикет, а не initData/cookie.** Браузерный WebSocket не шлёт заголовки (`tma` Mini App), поэтому хаб
  принимает только 60-секундный HMAC-тикет (`access_token` в query или `Bearer`). Ключ — связка
  `Attachments:DownloadSigningKey` с префиксом payload `hub:`. `access_token` маскируется `LogPathMasker`.
  JS-клиент зовёт `accessTokenFactory` и на negotiate, и на сам сокет — поэтому тикет многоразовый в пределах TTL.
- **Гонка счётчика на старте** (поймана e2e): `start()` спрашивает непрочитанное до того, как соединение вошло в
  группы; сообщение в этом окне не давало ни события, ни бейджа. Поэтому после `connection.start()` счётчик
  перечитывается ещё раз.
- **Лимит тела запроса.** Глобальный лимит Kestrel = одно мед-вложение + 5 МБ (~10 МБ). Эндпоинт отправки
  поднимает свой через `RequestSizeLimitAttribute` + `RequestFormLimitsAttribute` (маршрутизация ASP.NET
  учитывает `IRequestSizeLimitMetadata`). TestServer лимиты Kestrel не применяет — интеграционный тест
  это не доказывает (TECH_DEBT #20).
- **`vi.mock` не работает** в `@angular/build:unit-test` (зависимости уже в бандле) — поэтому клиент SignalR
  грузится через токен `SIGNALR_LOADER`, а тест подставляет фейковый хаб.
- **Курсор ленты** — `"{CreatedAt.Ticks}_{Id:N}"`, фильтр `CreatedAt < c || (== && Id.CompareTo(c.Id) < 0)`
  (Npgsql транслирует `CompareTo` в сравнение uuid; порядок — порядок БД, согласованный с `ORDER BY`).
- **Непрочитанное** не считает свои, удалённые и сообщения раньше `FamilyMember.JoinedAt` — новичку не
  насчитывается вся история.
- **Удаление мягкое**: текст пустой, реакции/ссылки/вложения (и блобы) стёрты, строка осталась под цитаты.
  Удаление аккаунта — жёсткое (каскад FK), ответы на его сообщения теряют цитату (`ReplyToMessageId` SetNull).
- **Внешние каналы без имён**: колокольчик — «Имя написал(а) в чат семьи «X»», Telegram/push — только
  «новое сообщение в чате семьи» (политика конфиденциальности, как у всех типов).
- **Ссылка на чужую мед-запись** в карточке пишет аудит `ViewList`, как строка списка записей.
- **Dev/e2e:** прокси `/hubs` с `ws: true` (`proxy.conf.js`); Caddy WebSocket проксирует сам.

## Тесты

- Интеграционные: `tests/FamilyHub.IntegrationTests/ChatApiTests.cs` (права, реакции, вложения, ссылки,
  непрочитанное, курсор, хаб по тикету через long polling поверх `Factory.Server.CreateHandler()`, задача
  уведомлений, удаление семьи).
- Юнит: `HubTicketServiceTests`, `ChatMessageNotifyJobTests`.
- Vitest: `chat-realtime.service.spec.ts`, `related-kind-route.spec.ts`.
- E2E: `e2e/tests/23-family-chat.spec.ts` (два браузерных контекста), `23-family-chat-mobile.spec.ts`.
