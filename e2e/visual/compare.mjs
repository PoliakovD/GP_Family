// Склейки «A | B» для ревью редизайна: снимки до/после или макет/после.
//
//   node visual/compare.mjs <папка-A> <папка-B> <папка-результата> [подпись-A] [подпись-B]
//
// Берёт одноимённые PNG из обеих папок и кладёт рядом (с подписями) в один PNG. Если в A и B
// разные имена (макет `home-desktop.png` ↔ снимок `home-desktop.png`) — достаточно совпадения имён.
// Изображения встраиваются data-URL'ами в страницу Chromium — без зависимостей вроде sharp.
import { chromium } from '@playwright/test';
import { readdir, readFile, mkdir } from 'node:fs/promises';
import path from 'node:path';

const [dirA, dirB, outDir, labelA = path.basename(dirA), labelB = path.basename(dirB)] = process.argv.slice(2);
if (!dirA || !dirB || !outDir) {
  console.error('node visual/compare.mjs <A> <B> <out> [подпись-A] [подпись-B]');
  process.exit(1);
}
await mkdir(outDir, { recursive: true });

const namesB = new Set(await readdir(dirB));
const names = (await readdir(dirA)).filter((n) => n.endsWith('.png') && namesB.has(n));
const browser = await chromium.launch(process.env.CHROMIUM ? { executablePath: process.env.CHROMIUM } : {});
const page = await browser.newPage({ deviceScaleFactor: 1 });

for (const name of names) {
  const [a, b] = await Promise.all([readFile(path.join(dirA, name)), readFile(path.join(dirB, name))]);
  const same = a.equals(b);
  await page.setContent(`<!doctype html><html><body style="margin:0;background:#888;font:600 15px system-ui">
    <div id="row" style="display:inline-flex;gap:16px;padding:12px;align-items:flex-start">
      ${[[labelA, a], [labelB, b]].map(([l, buf]) => `<figure style="margin:0">
        <figcaption style="color:#fff;margin-bottom:6px">${l} — ${name}${same ? ' (без изменений)' : ''}</figcaption>
        <img style="display:block" src="data:image/png;base64,${buf.toString('base64')}"></figure>`).join('')}
    </div></body></html>`);
  await page.waitForFunction(() => [...document.images].every((i) => i.complete));
  await page.locator('#row').screenshot({ path: path.join(outDir, name) });
  console.log(path.join(outDir, name) + (same ? '  (без изменений)' : ''));
}
await browser.close();
