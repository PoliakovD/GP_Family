import { test, expect } from '@playwright/test';
import { createFamily, newUser, openAs } from '../support/api';

// На телефоне страница не должна быть шире экрана: иначе мобильный Chrome раздувает layout-viewport,
// содержимое «уезжает» влево, а клики по нижней панели промахиваются (регрессия шапки с двумя кнопками).
async function expectNoHorizontalOverflow(page: import('@playwright/test').Page): Promise<void> {
  await page.waitForTimeout(800);
  const { inner, scroll } = await page.evaluate(() => ({ inner: window.innerWidth, scroll: document.documentElement.scrollWidth }));
  expect(scroll, `scrollWidth ${scroll} > innerWidth ${inner}`).toBeLessThanOrEqual(inner);
}

test('мобильные экраны здоровья не шире экрана', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await createFamily(api, 'Семья Орловых');

  for (const url of [
    '/home', '/health/medications', '/health/intake', '/health/intake/courses', '/health/records', '/health/visits',
    '/health/notes', '/health/reports', '/health/indicators', '/notifications', '/settings/profile',
  ]) {
    await openAs(page, tgId, url);
    await expectNoHorizontalOverflow(page);
  }
});

test('мобильный приём лекарств: форма курса открывается нижним листом и закрывается', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await createFamily(api, 'Семья Орловых');

  await openAs(page, tgId, '/health/intake');
  await page.getByRole('button', { name: 'Новый курс' }).click();
  await expect(page.getByPlaceholder('Например, Сорбифер Дурулес 100 мг')).toBeVisible();

  await page.getByRole('button', { name: 'Отмена' }).click();
  await expect(page.getByPlaceholder('Например, Сорбифер Дурулес 100 мг')).toHaveCount(0);
});

test('мобильная нижняя панель: Здоровье → Приём лекарств через вкладки хаба', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await createFamily(api, 'Семья Орловых');

  await openAs(page, tgId, '/home');
  await page.locator('nav.tab-bar').getByRole('button', { name: 'Здоровье' }).click();
  await page.getByRole('link', { name: 'Приём лекарств' }).click();
  await expect(page).toHaveURL(/health\/intake/);
  await expect(page.getByText('Пока нет курсов приёма')).toBeVisible();
});
