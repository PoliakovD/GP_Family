// Лёгкий service worker ТОЛЬКО для push-уведомлений — без кэширования, без fetch-перехвата.
// Не заменяет ngsw-worker.js (Angular; офлайн-кэш оболочки) — тот доступен только в production
// ng build и не собирается под `ng serve`. Регистрируется вручную PushNotificationService, когда
// ngsw неактивен (dev-режим, напр. текущий сетап через `ng serve` + ngrok-туннель для тестирования
// push и вебхуков одновременно с dev-панелью, см. push-notification.service.ts).
//
// Формат payload идентичен тому, что уже понимает ngsw "из коробки" — WebPushNotificationSender на
// бэкенде шлёт именно его: {"notification":{"title":..,"body":..,"icon":..,"data":{...}}}.
// Это не отдельный контракт, а совместимая реализация того же формата под конкретную SW-регистрацию.
self.addEventListener('push', (event) => {
  let payload = {};
  try {
    payload = event.data ? event.data.json() : {};
  } catch {
    payload = {};
  }
  const n = payload.notification || {};

  event.waitUntil(
    self.registration.showNotification(n.title || 'FamilyHub', {
      body: n.body || '',
      icon: n.icon || '/icons/icon-192.png',
      data: n.data || {},
      // Кнопки, тег и тихий режим (напоминания о приёме лекарств, ADR-0015) — те же поля, что пропускает ngsw.
      actions: n.actions || [],
      tag: n.tag,
      renotify: !!n.renotify,
      requireInteraction: !!n.requireInteraction,
      silent: !!n.silent,
    }),
  );
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const onActionClick = event.notification.data?.onActionClick || {};
  // Пустое action — клик по самому уведомлению («default»), иначе — по кнопке.
  const handler = onActionClick[event.action || 'default'];

  // Кнопка «Принял/Отложить/Пропустить»: фоновый запрос без открытия приложения — как операция
  // sendRequest в ngsw. Авторизует одноразовый токен в URL, куки не нужны.
  if (handler && handler.operation === 'sendRequest') {
    event.waitUntil(
      fetch(new URL(handler.url, self.registration.scope).href, { credentials: 'omit' }).catch(() => {}),
    );
    return;
  }

  const url = (handler && handler.url) || onActionClick.default?.url || '/notifications';

  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((clientsList) => {
      for (const client of clientsList) {
        if ('focus' in client) {
          if ('navigate' in client) client.navigate(url);
          return client.focus();
        }
      }
      return self.clients.openWindow(url);
    }),
  );
});
