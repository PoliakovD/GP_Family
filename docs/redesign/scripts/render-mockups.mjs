// Рендер эталонных макетов «Гибрид A+B» из экспорта холста в отдельные PNG.
//
//   node docs/redesign/scripts/render-mockups.mjs [путь-к-html] [папка-png]
//
// Экспорт — самораспаковывающийся бандл: после загрузки страница содержит 8 <section.board>,
// в каждой <h2> с подписью и <iframe> с экраном (1440×1000 или 390×844). Скрипт снимает
// каждый iframe по отдельности; если контент выше рамки (дневник-десктоп: 1054 > 1000),
// iframe растягивается до полной высоты контента, чтобы ничего не обрезалось.
// Playwright берётся из PLAYWRIGHT_MODULE (путь к пакету) или из `playwright`/`@playwright/test`.
import { createRequire } from 'module';
import path from 'path';
import { fileURLToPath } from 'url';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
function loadPlaywright() {
  for (const id of [process.env.PLAYWRIGHT_MODULE, 'playwright', '@playwright/test',
    path.join(here, '../../../e2e/node_modules/@playwright/test')]) {
    if (!id) continue;
    try { return require(id); } catch { /* следующий вариант */ }
  }
  throw new Error('Playwright не найден: задайте PLAYWRIGHT_MODULE=/путь/к/node_modules/playwright');
}
const { chromium } = loadPlaywright();

const input = path.resolve(process.argv[2] ?? path.join(here, '../screens/FamilyHub — направления редизайна.html'));
const outDir = path.resolve(process.argv[3] ?? path.join(here, '../screens/png'));

// <title> документа внутри iframe → имя файла.
const NAMES = {
  'Гибрид — Главная': 'home-desktop',
  'Гибрид — Главная, мобайл': 'home-mobile',
  'Гибрид — Семья и чат': 'family-chat-desktop',
  'Гибрид — Чат семьи, мобайл': 'family-chat-mobile',
  'Гибрид — Анализ': 'analysis-desktop',
  'Гибрид — Анализ, мобайл': 'analysis-mobile',
  'Гибрид — Дневник': 'diary-desktop',
  'Гибрид — Дневник, мобайл': 'diary-mobile',
};

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 });
await page.goto('file://' + input);
await page.waitForSelector('section.board iframe', { timeout: 30_000 });
await page.waitForTimeout(3000); // распаковка бандла и загрузка iframe

for (const handle of await page.$$('section.board iframe')) {
  const frame = await handle.contentFrame();
  await frame.evaluate(() => document.fonts.ready);
  const title = await frame.evaluate(() => document.title);
  const name = NAMES[title];
  if (!name) { console.warn('Пропущен экран с неизвестной подписью:', title); continue; }

  const contentH = await frame.evaluate(() => document.documentElement.scrollHeight);
  const frameH = await handle.evaluate((el) => el.clientHeight);
  if (contentH > frameH) await handle.evaluate((el, h) => { el.style.height = h + 'px'; }, contentH);
  await handle.scrollIntoViewIfNeeded();
  await page.waitForTimeout(1500); // после изменения размера iframe перерисовывается не сразу

  await handle.screenshot({ path: path.join(outDir, `${name}.png`) });
  const box = await handle.boundingBox();
  console.log(`${name}.png  ${Math.round(box.width)}×${Math.round(box.height)}`
    + (contentH > frameH ? `  (рамка ${frameH}, контент ${contentH})` : ''));
}
await browser.close();
