// Скриншоты экранов для редизайна (docs/redesign/PLAN.md, «Как проверяем каждую фазу»).
//
// В отличие от сценариев e2e/tests, бэкенд не нужен: собранный фронт (`ng build`, по умолчанию
// src/FamilyHub.Api/wwwroot) раздаётся статикой, а /api/** отвечает Playwright из фикстур
// visual/fixtures.mjs — данные повторяют макеты (семья «Поляковы», анализ крови, дневник…),
// поэтому снимок можно положить рядом с docs/redesign/screens/png/*.png.
//
//   node visual/shots.mjs <метка> [фильтр-экранов]
//     метка  — подпапка результата: .artifacts/shots/<метка>/<экран>-<desktop|mobile>.png
//     фильтр — подстрока имени экрана (необязательно)
//
// Переменные: SPA_DIR — каталог сборки; THEME=dark — тёмная тема; CHROMIUM — путь к браузеру, если версия Playwright
// не совпадает со скачанными браузерами (в облачном окружении — /opt/pw-browsers/chromium).
import { chromium } from '@playwright/test';
import { createServer } from 'node:http';
import { readFile, mkdir, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { handleApi, SCREENS } from './fixtures.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const spaDir = path.resolve(process.env.SPA_DIR ?? path.join(here, '../../src/FamilyHub.Api/wwwroot'));
const label = process.argv[2] ?? 'current';
const filter = process.argv[3] ?? '';
const outDir = path.join(here, '../.artifacts/shots', label);

const VIEWPORTS = {
  desktop: { width: 1440, height: 1000 },
  mobile: { width: 390, height: 844 },
};

const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json',
  '.webmanifest': 'application/manifest+json', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon',
  '.woff2': 'font/woff2', '.woff': 'font/woff', '.mjs': 'text/javascript' };

// Статика с SPA-fallback: неизвестный путь без расширения → index.html.
const server = createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');
  let file = path.join(spaDir, decodeURIComponent(url.pathname));
  try {
    if ((await stat(file)).isDirectory()) file = path.join(file, 'index.html');
  } catch {
    file = path.join(spaDir, 'index.html');
  }
  try {
    const body = await readFile(file);
    res.writeHead(200, { 'content-type': MIME[path.extname(file)] ?? 'application/octet-stream' });
    res.end(body);
  } catch {
    res.writeHead(404).end();
  }
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const base = `http://127.0.0.1:${server.address().port}`;

const browser = await chromium.launch(process.env.CHROMIUM ? { executablePath: process.env.CHROMIUM } : {});
await mkdir(outDir, { recursive: true });
const unmatched = new Set();

for (const screen of SCREENS.filter((s) => s.name.includes(filter))) {
  for (const [vpName, viewport] of Object.entries(VIEWPORTS)) {
    if (screen.only && screen.only !== vpName) continue;
    const context = await browser.newContext({
      viewport, deviceScaleFactor: 1, locale: 'ru-RU', timezoneId: 'Europe/Moscow',
      hasTouch: vpName === 'mobile', isMobile: vpName === 'mobile',
      serviceWorkers: 'block',
    });
    // Фиксированное «сегодня» — как в макетах (среда, 14 октября 2026, 09:40).
    await context.clock.setFixedTime(new Date('2026-10-14T09:40:00+03:00'));
    await context.addInitScript(() => {
      localStorage.setItem('familyhub:cookieConsent', 'accepted');
    });
    // THEME=dark — снимок в тёмной теме (styles.scss, :root[data-theme="dark"]).
    if (process.env.THEME) {
      await context.addInitScript((theme) => {
        const apply = () => document.documentElement?.setAttribute('data-theme', theme);
        apply();
        addEventListener('DOMContentLoaded', apply);
      }, process.env.THEME);
    }
    // Dev-сборка показывает DevPanel (кнопка в углу) — на снимках она не нужна.
    await context.addInitScript(() => {
      addEventListener('DOMContentLoaded', () => {
        const style = document.createElement('style');
        style.textContent = 'app-dev-panel { display: none !important; }';
        document.head.append(style);
      });
    });
    await context.route('**/api/**', async (route) => {
      const req = route.request();
      const url = new URL(req.url());
      const result = handleApi(req.method(), url.pathname, url.searchParams, req.postDataJSON?.());
      if (result === undefined) {
        unmatched.add(`${req.method()} ${url.pathname}`);
        return route.fulfill({ status: 404, json: { title: 'нет фикстуры' } });
      }
      // { rawText } — ответ не JSON (политика конфиденциальности отдаётся как HTML-текст).
      if (typeof result.rawText === 'string') return route.fulfill({ status: 200, contentType: result.contentType ?? 'text/html', body: result.rawText });
      // { status: число, body } — ответ с кодом (ошибка); иначе сам объект — тело 200 (у данных бывает своё поле status).
      const wrapped = typeof result.status === 'number' && 'body' in result;
      return route.fulfill({ status: wrapped ? result.status : 200, json: wrapped ? result.body : result });
    });
    // overrides — свои ответы только для этого экрана (как ROUTES): маршрут, добавленный позже, приоритетнее.
    for (const [m, re, res] of screen.overrides?.() ?? []) {
      await context.route((u) => re.test(u.pathname), (route) => route.request().method() === m
        ? route.fulfill({ json: res }) : route.fallback());
    }
    // anon — экран для человека без сессии (вход, приглашение): маршрут, добавленный позже, приоритетнее.
    if (screen.anon) await context.route('**/api/auth/me', (route) => route.fulfill({ status: 401, json: {} }));
    // Всё внешнее (шрифты/аналитика/проверки связи) — не ходим в сеть: снимок не должен зависеть от неё.
    await context.route((url) => !url.href.startsWith(base), (route) => route.abort());
    // SignalR чата: без сервера просто падает в «нет соединения» — не мешает снимку.
    await context.route('**/hubs/**', (route) => route.abort());

    const page = await context.newPage();
    page.on('pageerror', (e) => console.warn(`[${screen.name}-${vpName}] ошибка страницы: ${e.message}`));
    page.on('console', (m) => { if (m.type() === 'error' && !/negotiation|status of 503/.test(m.text())) console.warn(`[${screen.name}-${vpName}] console: ${m.text().slice(0, 300)}`); });
    await page.goto(base + screen.path);
    await page.waitForLoadState('networkidle');
    // Ждём, пока уйдут спиннеры «Загрузка…», иначе снимок зависит от тайминга.
    if (!screen.spinner) await page.waitForFunction(() => !document.querySelector('app-loading-spinner'), null, { timeout: 10_000 })
      .catch(() => console.warn(`[${screen.name}-${vpName}] спиннер не исчез за 10 с — снимок как есть`));
    if (screen.prepare) await screen.prepare(page, vpName);
    await page.evaluate(() => document.fonts.ready);
    await page.waitForTimeout(400);
    const file = path.join(outDir, `${screen.name}-${vpName}.png`);
    await page.screenshot({ path: file, fullPage: true });
    console.log(path.relative(process.cwd(), file));
    await context.close();
  }
}

await browser.close();
server.close();
if (unmatched.size) {
  console.warn('\nЗапросы без фикстуры (ответ 404):');
  for (const u of [...unmatched].sort()) console.warn('  ' + u);
}
