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
