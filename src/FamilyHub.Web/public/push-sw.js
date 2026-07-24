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
    }),
  );
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = event.notification.data?.onActionClick?.default?.url || '/notifications';

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
