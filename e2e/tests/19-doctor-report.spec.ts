import { test, expect } from '@playwright/test';
import { createAnalysis, newUser, openAs } from '../support/api';

/**
 * Отчёт для врача. В e2e-стеке нет Gotenberg (Previews:GotenbergBaseUrl пуст → NullGotenbergConverter),
 * поэтому сам PDF не собирается — проверяем то, что видит пользователь до него: счётчик данных за
 * период, «нет данных» за пустой период и понятную ошибку вместо падения, когда сервис PDF недоступен.
 */
test('отчёт для врача: счётчик за период, пустой период и недоступный сервис PDF', async ({ page }) => {
  const { tgId, api } = await newUser('Врачёв', 'Виктор');
  await createAnalysis(api, 'Общий анализ крови'); // дата записи — 2026-06-09

  await openAs(page, tgId, '/health/reports');
  await expect(page.getByRole('heading', { name: 'Пока нет отчётов' })).toBeVisible();
  await page.getByRole('button', { name: 'Сформировать первый отчёт' }).click();

  const form = page.locator('app-doctor-report-form');
  // Сначала «по», затем «с»: у полей взаимные min/max.
  await form.getByLabel('Конец периода').fill('2026-06-30');
  await form.getByLabel('Начало периода').fill('2026-06-01');
  await expect(form.getByText('За период: 1 анализ, 0 приёмов, 0 записей дневника')).toBeVisible();

  // Перепутанные даты — кнопка неактивна и есть подсказка.
  await form.getByLabel('Начало периода').fill('2026-07-15');
  await expect(form.getByText(/Проверьте период/)).toBeVisible();
  await expect(form.getByRole('button', { name: 'Сформировать PDF' })).toBeDisabled();

  // Пустой период — сервер отвечает «нет данных», форма показывает это, а не падает.
  await form.getByLabel('Начало периода').fill('2020-01-01');
  await form.getByLabel('Конец периода').fill('2020-01-31');
  await expect(form.getByText('За период: 0 анализов, 0 приёмов, 0 записей дневника')).toBeVisible();
  await form.getByRole('button', { name: 'Сформировать PDF' }).click();
  await expect(form.locator('.alert-danger')).toContainText('нет данных для отчёта');

  // С жалобами отчёту есть что показать, но сервис PDF недоступен — понятная ошибка, а не падение;
  // отчёт в списке не появляется. (Динамика анализов строится по распознанным показателям — без ИИ её нет.)
  await form.getByLabel(/Жалобы и вопросы к врачу/).fill('Часто болит голова по утрам');
  await form.getByRole('button', { name: 'Сформировать PDF' }).click();
  await expect(form.locator('.alert-danger')).toContainText('сервис документов временно недоступен');

  await form.getByRole('button', { name: 'Отмена' }).click();
  await expect(page.getByRole('heading', { name: 'Пока нет отчётов' })).toBeVisible();
  expect(await (await api.get('/api/doctor-reports')).json()).toEqual([]);
});
