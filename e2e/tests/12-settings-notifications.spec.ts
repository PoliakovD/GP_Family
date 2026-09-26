import { test, expect } from '@playwright/test';
import { newUser, openAs } from '../support/api';

test('настройки: аккаунт, безопасность, оповещения и данные открываются', async ({ page }) => {
  const { tgId } = await newUser('Орлов', 'Олег');

  await openAs(page, tgId, '/settings/profile');
  await expect(page.getByText('Орлов Олег').first()).toBeVisible();

  await page.getByRole('link', { name: 'Безопасность' }).first().click();
  await expect(page).toHaveURL(/settings\/security/);
  await expect(page.getByText(/Пароль и активные сессии/)).toBeVisible();

  await page.getByRole('link', { name: 'Оповещения' }).first().click();
  await expect(page).toHaveURL(/settings\/notifications/);
  await expect(page.getByText(/Push-уведомления выключены/)).toBeVisible();

  await page.getByRole('link', { name: 'Данные и приватность' }).first().click();
  await expect(page).toHaveURL(/settings\/data/);
  await expect(page.getByText('Выгрузить мои данные (zip)')).toBeVisible();
  await expect(page.getByText('Удаление аккаунта')).toBeVisible();
});

test('оповещения: пустая лента; политика конфиденциальности открывается', async ({ page }) => {
  const { tgId } = await newUser('Орлов', 'Олег');

  await openAs(page, tgId, '/notifications');
  await expect(page.getByText('Оповещений нет.')).toBeVisible();

  await openAs(page, tgId, '/privacy');
  await expect(page.getByText('Политика конфиденциальности FamilyHub')).toBeVisible();
});

test('экспорт данных отдаёт zip', async ({ page }) => {
  const { tgId } = await newUser('Орлов', 'Олег');
  await openAs(page, tgId, '/settings/data');

  const [download] = await Promise.all([
    page.waitForEvent('download'),
    page.getByText('Выгрузить мои данные (zip)').click(),
  ]);
  expect(download.suggestedFilename()).toMatch(/\.zip$/);
});
