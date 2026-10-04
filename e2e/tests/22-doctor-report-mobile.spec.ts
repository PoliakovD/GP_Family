import { test, expect } from '@playwright/test';
import { newUser, openAs } from '../support/api';

test('мобильный отчёт для врача: кнопка внизу открывает форму нижним листом и закрывает её', async ({ page }) => {
  const { tgId } = await newUser('Мобильный', 'Марк');

  await openAs(page, tgId, '/health/reports');
  await expect(page.getByRole('heading', { name: 'Пока нет отчётов' })).toBeVisible();

  // На узком экране — плавающая кнопка, форма — в нижнем листе «Новый отчёт», а не в боковой панели.
  await page.locator('.dr-fab').click();
  const form = page.locator('app-doctor-report-form');
  await expect(page.getByText('Новый отчёт', { exact: true })).toBeVisible();
  await expect(form.getByRole('button', { name: 'Сформировать PDF' })).toBeVisible();

  // Форма не шире экрана.
  const width = page.viewportSize()!.width;
  const box = await form.boundingBox();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(width + 1);

  await form.getByRole('button', { name: 'Отмена' }).click();
  await expect(form).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Пока нет отчётов' })).toBeVisible();
});
