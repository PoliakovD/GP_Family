import { test, expect } from '@playwright/test';
import { attachFile, createAnalysis, newUser, openAs } from '../support/api';

// В e2e-стеке LM Studio направлен на закрытый порт — ИИ «недоступен».

test('ИИ недоступен: глобальная плашка и документ ждёт в очереди', async ({ page }) => {
  const { tgId, api } = await newUser('Кузнецов', 'Кирилл');
  const recordId = await createAnalysis(api, 'Анализ мочи');
  await attachFile(api, recordId);

  // Пока у пользователя нет фоновых задач, плашку не показываем — новичку она выглядела бы как «приложение сломано».
  const aiStatus = page.waitForResponse((r) => r.url().includes('/api/ai/status'));
  await openAs(page, tgId, '/home');
  expect(await (await aiStatus).json()).toEqual({ available: false });
  await expect(page.locator('.ai-banner')).toHaveCount(0);

  // Распознавание не отказывает, а ставит задачу в ожидание (202 waiting_for_ai).
  const res = await api.post(`/api/medical-records/${recordId}/extract`);
  expect(res.status()).toBe(202);
  expect(((await res.json()) as { code: string }).code).toBe('waiting_for_ai');

  // Теперь задача есть и ждёт ИИ — глобальная плашка объясняет, почему «в процессе».
  await page.reload();
  await expect(page.locator('.ai-banner')).toContainText('ИИ временно недоступен');

  // На записи виден шаг «Ждём ИИ», а не ошибка.
  await page.goto(`/health/records/${recordId}`);
  await expect(page.getByText(/Ждём ИИ/).first()).toBeVisible();
  await expect(page.locator('.alert-danger')).toHaveCount(0);

  // В трее фоновых задач она видна как ожидающая ИИ.
  const tray = await (await api.get('/api/jobs/active-summary')).json();
  expect(tray.extraction.total).toBe(1);
  expect(tray.extraction.items[0].waitingForAi).toBe(true);
});

test('удаление записи убирает её задачу распознавания из трея', async ({ page }) => {
  const { tgId, api } = await newUser('Морозов', 'Максим');
  const recordId = await createAnalysis(api, 'Анализ крови');
  await attachFile(api, recordId);
  expect((await api.post(`/api/medical-records/${recordId}/extract`)).status()).toBe(202);
  expect((await (await api.get('/api/jobs/active-summary')).json()).extraction.total).toBe(1);

  await openAs(page, tgId, `/health/records/${recordId}`);
  await page.getByRole('button', { name: 'Удалить' }).click();
  await page.locator('.overlay-card').getByRole('button', { name: 'Удалить' }).click();
  await expect(page).toHaveURL(/\/health\/records$/);

  expect((await (await api.get('/api/jobs/active-summary')).json()).extraction.total).toBe(0);
});
