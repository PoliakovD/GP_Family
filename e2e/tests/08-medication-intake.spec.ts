import { test, expect } from '@playwright/test';
import { newUser, openAs } from '../support/api';

// Приём лекарств (ADR-0015): курс «по необходимости» создаётся через API, отмечается на экране «Сегодня»,
// запись попадает в дневник. Плановые приёмы зависят от времени суток, поэтому сквозной сценарий — на
// приёме без расписания (детерминирован в любой час).
test('приём лекарств: курс по необходимости отмечается в «Сегодня» и попадает в дневник', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  const created = await api.post('/api/medication-courses', {
    data: {
      dependentId: null,
      drugName: 'Нурофен 200 мг',
      schedule: { mode: 4, maxPerDay: 3, intervalUnits: 1 },
      food: 0,
      unit: 0,
      startDate: new Date().toISOString().slice(0, 10),
      endDate: null,
      medicationId: null,
      writeOff: false,
      repeatAfterMinutes: null,
      missedAfterMinutes: 120,
      lowStockDays: 5,
      sourceMedicalRecordId: null,
      sourcePrescriptionIndex: null,
      prescriptionText: null,
      notes: null,
    },
  });
  expect(created.status()).toBe(201);

  await openAs(page, tgId, '/health/intake');
  await expect(page.getByText(/По необходимости:/)).toContainText('Нурофен 200 мг');
  await expect(page.getByText(/сегодня 0 из 3/)).toBeVisible();

  await page.getByRole('button', { name: 'Отметить приём' }).click();
  await expect(page.getByText(/сегодня 1 из 3/)).toBeVisible();

  const notes = await (await api.get('/api/health-notes')).json();
  expect(notes.some((n: { title: string }) => n.title === 'Нурофен 200 мг')).toBe(true);
});

test('приём лекарств: вкладка «Курсы» показывает созданный курс и его карточку', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await api.post('/api/medication-courses', {
    data: {
      dependentId: null,
      drugName: 'Сорбифер Дурулес 100 мг',
      schedule: { mode: 0, times: [{ at: '08:00:00', units: 1 }, { at: '20:00:00', units: 1 }] },
      food: 1,
      unit: 0,
      startDate: new Date().toISOString().slice(0, 10),
      endDate: null,
      medicationId: null,
      writeOff: false,
      repeatAfterMinutes: 15,
      missedAfterMinutes: 120,
      lowStockDays: 5,
      sourceMedicalRecordId: null,
      sourcePrescriptionIndex: null,
      prescriptionText: null,
      notes: null,
    },
  });

  await openAs(page, tgId, '/health/intake/courses');
  await expect(page.getByText('Сорбифер Дурулес 100 мг').first()).toBeVisible();
  await expect(page.getByText('8:00 и 20:00')).toBeVisible();
});
