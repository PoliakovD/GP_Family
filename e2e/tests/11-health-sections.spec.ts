import { test, expect } from '@playwright/test';
import { createFamily, newUser, openAs } from '../support/api';

test('дневник: запись из API видна в ленте, новая заметка добавляется из формы', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  const res = await api.post('/api/health-notes', {
    data: {
      kind: 0, occurredAt: new Date(Date.now() - 600_000).toISOString(), title: 'Головная боль', text: null,
      includeInDoctorQuestions: false, symptom: { severity: 6, areas: ['head'], detail: null },
    },
  });
  expect(res.status()).toBe(201);

  await openAs(page, tgId, '/health/notes');
  await expect(page.getByText('Головная боль').first()).toBeVisible();
  await expect(page.getByText(/Видите только вы/)).toBeVisible();

  await page.getByRole('button', { name: /Добавить запись/ }).first().click();
  await page.getByRole('radio', { name: 'Заметка' }).click();
  await page.locator('textarea').first().fill('Вопрос врачу про давление');
  await page.getByRole('button', { name: /Сохранить|Добавить/ }).last().click();
  await expect(page.getByText('Вопрос врачу про давление').first()).toBeVisible();
});

test('отчёты для врача: экран открывается, публичная ссылка с неверным токеном недоступна', async ({ page }) => {
  const { tgId } = await newUser('Орлов', 'Олег');

  await openAs(page, tgId, '/health/reports');
  await expect(page.getByText(/Отчёт/).first()).toBeVisible();

  await page.goto('/r/no-such-token');
  await expect(page.getByText(/недоступна|не найден|истёк|отозван/i).first()).toBeVisible();
});

test('посещения врачей, справочник и показатели: экраны открываются', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await createFamily(api, 'Семья Орловых');

  await openAs(page, tgId, '/health/visits');
  await expect(page.getByText('Добавить посещение').first()).toBeVisible();

  await openAs(page, tgId, '/health/kb');
  await expect(page.getByText('Справочник').first()).toBeVisible();

  await openAs(page, tgId, '/health/indicators');
  await expect(page.getByText(/Показателей пока нет/)).toBeVisible();
});
