// Service worker FamilyHub (этап 2 п.2.4, PWA).
// Намеренно минимальный: только офлайн-заглушка для навигации.
// ВАЖНОЕ ограничение безопасности: /api НИКОГДА не кэшируется — медицинские данные
// не должны оседать в Cache Storage браузера (152-ФЗ, задача 2.2).
const CACHE = 'familyhub-shell-v1';

self.addEventListener('install', (event) => {
  self.skipWaiting();
  event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(['/'])));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((keys) =>
      Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))),
    ),
  );
});

self.addEventListener('fetch', (event) => {
  const url = new URL(event.request.url);

  // Данные и файлы — всегда только сеть, без кэша.
  if (url.pathname.startsWith('/api/')) return;

  // Навигация: сеть с офлайн-фолбэком на закэшированную оболочку.
  if (event.request.mode === 'navigate') {
    event.respondWith(fetch(event.request).catch(() => caches.match('/')));
  }
});
