import { test, expect } from '@playwright/test';
import { createFamily, newUser, openAs } from '../support/api';

test('приём лекарств: курс создаётся из формы и появляется во вкладке «Курсы»', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await createFamily(api, 'Семья Орловых');

  await openAs(page, tgId, '/health/intake');
  await expect(page.getByText('Пока нет курсов приёма')).toBeVisible();

  await page.getByRole('button', { name: 'Новый курс' }).click();
  await page.getByRole('radio', { name: /Вручную/ }).click();
  await page.getByPlaceholder('Например, Сорбифер Дурулес 100 мг').fill('Витамин D3 2000 МЕ');
  await page.getByRole('radio', { name: 'По необходимости' }).click();
  await expect(page.getByText(/по необходимости, не чаще/i)).toBeVisible(); // итог расписания фразой
  await page.getByRole('button', { name: 'Начать курс' }).click();

  await expect(page.getByText('Курс создан — напомним о приёме')).toBeVisible();
  await page.getByRole('tab', { name: /Курсы/ }).click();
  await expect(page.getByText('Витамин D3 2000 МЕ').first()).toBeVisible();
});

test('приём лекарств: настройки напоминаний открываются и сохраняют тихие часы', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await createFamily(api, 'Семья Орловых');
  await api.post('/api/medication-courses', {
    data: {
      dependentId: null, drugName: 'Сорбифер', schedule: { mode: 0, times: [{ at: '08:00:00', units: 1 }] },
      food: 0, unit: 0, startDate: new Date().toISOString().slice(0, 10), endDate: null, medicationId: null, writeOff: false,
      repeatAfterMinutes: 15, missedAfterMinutes: 120, lowStockDays: 5, sourceMedicalRecordId: null,
      sourcePrescriptionIndex: null, prescriptionText: null, notes: null,
    },
  });

  await openAs(page, tgId, '/health/intake/courses');
  await page.getByText('Настроить', { exact: true }).click();
  await expect(page.getByText('Кто узнает о моих пропусках')).toBeVisible();

  const saved = page.waitForResponse((r) => r.url().includes('/quiet-hours') && r.request().method() === 'PUT');
  await page.locator('.ir-quiet .switch').click();
  expect((await saved).status()).toBe(204);
  await page.reload();
  await page.getByText('Настроить', { exact: true }).click();
  await expect(page.locator('.ir-quiet input[type=checkbox]')).toBeChecked();
});

test('экраны добавления записи и посещения открываются', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await createFamily(api, 'Семья Орловых');

  await openAs(page, tgId, '/health/records/new');
  await expect(page).toHaveURL(/records\/new/);
  await expect(page.locator('main')).not.toBeEmpty();

  await openAs(page, tgId, '/health/visits/new');
  await expect(page).toHaveURL(/visits\/new/);
  await expect(page.locator('main')).not.toBeEmpty();
});
