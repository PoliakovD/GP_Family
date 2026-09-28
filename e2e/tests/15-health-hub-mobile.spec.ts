import { test, expect } from '@playwright/test';
import { newUser, openAs } from '../support/api';

// Хаб «Здоровье» на мобиле (макет «Screen - Health hub»): плитка ведёт в раздел, «‹ Здоровье» —
// обратно на хаб, таб «Здоровье» снизу остаётся активным всё это время.
test('мобильный хаб «Здоровье»: плитка → раздел → «‹ Здоровье» назад на хаб', async ({ page }) => {
  const { tgId } = await newUser('Орлов', 'Олег');

  await openAs(page, tgId, '/health');
  const tabs = page.locator('nav.tab-bar');
  await expect(tabs.getByRole('button', { name: 'Здоровье' })).toHaveClass(/active/);
  // «Моё здоровье» встречается дважды на этом экране (мобильная шапка и заголовок хаба) — берём
  // заголовок содержимого, он однозначен.
  await expect(page.getByRole('heading', { name: 'Моё здоровье' })).toBeVisible();

  await page.getByRole('link', { name: 'Дневник' }).click();
  await expect(page).toHaveURL(/\/health\/notes/);
  await expect(tabs.getByRole('button', { name: 'Здоровье' })).toHaveClass(/active/);

  // Сам back-link, не таб снизу — оба называются «Здоровье», поэтому locator сужен до контента.
  await page.locator('main.app-content').getByRole('button', { name: 'Здоровье' }).click();
  await expect(page).toHaveURL(/\/health$/);
});
