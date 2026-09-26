import { test, expect } from '@playwright/test';
import { createFamily, newUser, openAs } from '../support/api';

test('мобильная навигация: нижняя панель и переходы между разделами', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await createFamily(api, 'Семья Орловых');

  await openAs(page, tgId, '/home');
  const tabs = page.locator('nav.tab-bar');
  await expect(tabs).toBeVisible();
  for (const label of ['Главная', 'Здоровье', 'Семья', 'Ещё']) {
    await expect(tabs.getByRole('button', { name: label })).toBeVisible();
  }

  await tabs.getByRole('button', { name: 'Здоровье' }).click();
  await expect(page).toHaveURL(/\/health/);

  await tabs.getByRole('button', { name: 'Главная' }).click();
  await expect(page.getByRole('heading', { name: /Здравствуйте, Олег/ })).toBeVisible();
});

// Регрессия: закрытие листа «Ещё» снимало фиктивную запись истории через history.back() и откатывало
// переход по пункту — экран не менялся (см. HistoryDismissController.popOwnEntry).
test('мобильная навигация: пункты листа «Ещё» меняют экран', async ({ page }) => {
  const { tgId } = await newUser('Орлов', 'Олег');

  await openAs(page, tgId, '/health/records');
  const tabs = page.locator('nav.tab-bar');

  await tabs.getByRole('button', { name: 'Ещё' }).click();
  await page.getByRole('link', { name: 'Профиль' }).click();
  await expect(page).toHaveURL(/\/settings/);

  await tabs.getByRole('button', { name: 'Ещё' }).click();
  await page.getByRole('link', { name: 'Уведомления' }).click();
  await expect(page).toHaveURL(/\/notifications/);

  // Пункт, ведущий на текущий экран, просто закрывает лист.
  await tabs.getByRole('button', { name: 'Ещё' }).click();
  await page.getByRole('link', { name: 'Уведомления' }).click();
  await expect(page.locator('.sheet-panel')).toHaveCount(0);
  await expect(page).toHaveURL(/\/notifications/);
});
