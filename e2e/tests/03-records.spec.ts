import { test, expect } from '@playwright/test';
import { createAnalysis, newUser, openAs } from '../support/api';

test('запись анализа: список → открытая запись → удаление с подтверждением', async ({ page }) => {
  const { tgId, api } = await newUser('Петров', 'Пётр');
  await createAnalysis(api, 'Общий анализ крови');

  await openAs(page, tgId, '/health/records');
  await expect(page.getByText('Общий анализ крови').first()).toBeVisible();

  await page.getByRole('button', { name: 'Открыть' }).first().click();
  await expect(page).toHaveURL(/\/health\/records\/[0-9a-f-]{36}/);

  // Открытая запись: заголовок, ссылка назад, действия и мета-строка с врачом «Фамилия И.О.».
  await expect(page.getByRole('heading', { name: 'Общий анализ крови' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Анализы' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Редактировать' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Доступ' })).toBeVisible();
  await expect(page.locator('.record-detail-meta')).toContainText('Иванов И.И.');

  // Удаление: подтверждение → возврат к списку, записи больше нет.
  await page.getByRole('button', { name: 'Удалить' }).click();
  await expect(page.getByText('Удалить запись?')).toBeVisible();
  await page.locator('.overlay-card').getByRole('button', { name: 'Удалить' }).click();
  await expect(page).toHaveURL(/\/health\/records$/);
  await expect(page.getByText('Общий анализ крови')).toHaveCount(0);
});
