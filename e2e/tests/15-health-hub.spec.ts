import { test, expect } from '@playwright/test';
import { createFamily, newUser, openAs } from '../support/api';

// Хаб «Здоровье» (редизайн навигации, макет «Screen - Health hub») — десктоп: заголовок «Моё
// здоровье», три группы плиток (те же названия, что и в сайдбаре), клик по плитке предвыбирает «Я».
test('хаб «Здоровье»: заголовок, группы плиток и переход с предвыбором «Я»', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await createFamily(api, 'Семья Орловых');

  await openAs(page, tgId, '/health');
  // Сайдбар дублирует те же подписи своими ссылками — плитки хаба проверяем в контенте, не везде.
  const content = page.locator('main.app-content');
  await expect(content.getByRole('heading', { name: 'Моё здоровье' })).toBeVisible();
  await expect(content.getByText(/Олег, \d+ лет/)).toBeVisible();

  // Заголовки плиток (не весь текст ссылки — он ещё содержит превью и совпал бы по подстроке,
  // например «Показатели» с «лекарства и показатели» в подписи «Справочника»).
  for (const label of ['Приём лекарств', 'Дневник', 'Анализы', 'Приёмы врача', 'Показатели', 'Прививки', 'Аптечка', 'Отчёты для врача', 'Справочник']) {
    await expect(content.locator('.hh-tile-title, .hh-tool-title').getByText(label, { exact: true })).toBeVisible();
  }

  // Группы — те же подписи, что и в сайдбаре (app.component.ts healthGroups).
  const sidebar = page.locator('aside.app-sidebar');
  for (const group of ['Каждый день', 'Медкарта', 'Инструменты']) {
    await expect(sidebar.getByText(group, { exact: true })).toBeVisible();
  }

  await content.getByRole('link', { name: 'Анализы' }).click();
  await expect(page).toHaveURL(/health\/records\?person=me/);
});
