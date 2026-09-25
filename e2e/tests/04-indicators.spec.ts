import { test, expect } from '@playwright/test';
import { createAnalysis, newUser, openAs } from '../support/api';

test('показатель: добавление вручную → плитка «вне нормы» → панель справки с одним заголовком', async ({ page }) => {
  const { tgId, api } = await newUser('Сидоров', 'Сидор');
  const recordId = await createAnalysis(api, 'Биохимия');

  await openAs(page, tgId, `/health/records/${recordId}`);
  await page.getByRole('button', { name: 'Добавить показатель' }).click();
  await page.getByPlaceholder('Название показателя').fill('Гемоглобин');
  await page.getByPlaceholder('Значение').fill('90');
  await page.getByPlaceholder('Единица').fill('г/л');
  await page.getByPlaceholder('Норма от').fill('120');
  await page.getByPlaceholder('Норма до').fill('140');
  await page.getByRole('button', { name: 'Добавить', exact: true }).click();

  // Значение ниже нормы: плитка «вне нормы» показывает 1.
  await expect(page.locator('.record-status-tile-bad')).toContainText('1');
  await expect(page.locator('.record-status-tile-bad')).toContainText('вне нормы');

  // Клик по показателю открывает справку: значение в рамке, название — ровно один раз (в шапке панели).
  await page.getByText('Гемоглобин').first().click();
  const panel = page.locator('.indicator-info-panel');
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('Справочник FamilyHub');
  await expect(panel.locator('.kb-value')).toHaveText('90');
  await expect(panel.getByText('Гемоглобин', { exact: true })).toHaveCount(1);
});
