import { test, expect } from '@playwright/test';
import { createFamily, newUser, openAs } from '../support/api';

test('мобильная навигация: нижняя панель и переходы между разделами', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await createFamily(api, 'Семья Орловых');

  await openAs(page, tgId, '/home');
  const tabs = page.locator('nav.tab-bar');
  await expect(tabs).toBeVisible();
  for (const label of ['Главная', 'Здоровье', 'Семья', 'Профиль']) {
    await expect(tabs.getByRole('button', { name: label })).toBeVisible();
  }

  await tabs.getByRole('button', { name: 'Здоровье' }).click();
  await expect(page).toHaveURL(/\/health/);

  await tabs.getByRole('button', { name: 'Главная' }).click();
  await expect(page.getByRole('heading', { name: /Здравствуйте, Олег/ })).toBeVisible();
});

// Редизайн хаба «Здоровье» — лист «Ещё» упразднён: «Профиль» стал полноценным табом, «Уведомления»
// переехали в колокольчик мобильной шапки (см. app.component.html app-topbar-mobile).
test('мобильная навигация: таб «Профиль» и колокольчик уведомлений', async ({ page }) => {
  const { tgId } = await newUser('Орлов', 'Олег');

  await openAs(page, tgId, '/health/records');
  const tabs = page.locator('nav.tab-bar');

  await tabs.getByRole('button', { name: 'Профиль' }).click();
  await expect(page).toHaveURL(/\/settings/);

  // exact: в меню настроек есть ещё баннер «Уведомления выключены…» со ссылкой на настройки оповещений.
  await page.getByRole('link', { name: 'Уведомления', exact: true }).click();
  await expect(page).toHaveURL(/\/notifications/);
});
