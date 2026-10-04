import { test, expect, Page } from '@playwright/test';
import { newUser, openAs } from '../support/api';

/**
 * Добавление анализа и приёма врача из формы — с файлом бланка, но без распознавания: запись
 * сохраняется с тем, что ввёл пользователь, и в очередь ИИ ничего не уходит.
 */
async function fillRecordForm(page: Page, opts: { file: string; date: string; doctor: string; note: string }): Promise<void> {
  await page.locator('input[type=file]').setInputFiles({
    name: opts.file, mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4 e2e blank'),
  });
  await expect(page.getByText(opts.file)).toBeVisible();

  // Переключатель — label.switch вокруг скрытого чекбокса.
  const recognize = page.getByRole('checkbox', { name: 'Распознать бланк' });
  await expect(recognize).toBeChecked();
  await page.locator('label.switch', { has: recognize }).click();
  await expect(recognize).not.toBeChecked();

  await page.locator('input[type=date]').fill(opts.date);
  await page.getByRole('button', { name: 'Врач и заметка — необязательно' }).click();
  await page.getByRole('combobox').fill(opts.doctor);
  await page.getByRole('textbox', { name: /Описание|Заключение/ }).fill(opts.note);
}

test('анализ из формы: файл без распознавания → в списке и в открытой записи', async ({ page }) => {
  const { tgId, api } = await newUser('Формин', 'Фёдор');

  await openAs(page, tgId, '/health/records/new');
  await expect(page.getByRole('heading', { name: 'Добавить анализ' })).toBeVisible();
  await fillRecordForm(page, { file: 'blank.pdf', date: '2026-05-20', doctor: 'Сидорова Анна Петровна', note: 'Плановый осмотр' });
  await page.getByRole('main').getByRole('button', { name: 'Добавить анализ', exact: true }).click();

  await expect(page).toHaveURL(/\/health\/records$/);
  await expect(page.getByText('1 анализ', { exact: false }).first()).toBeVisible();
  await expect(page.getByText(/20 мая 2026/).first()).toBeVisible();
  await expect(page.getByText('Врач: Сидорова Анна Петровна')).toBeVisible();

  await page.getByRole('button', { name: 'Открыть' }).click();
  await expect(page).toHaveURL(/\/health\/records\/[0-9a-f-]{36}$/);
  await expect(page.getByText('Плановый осмотр')).toBeVisible();
  await expect(page.getByRole('main')).toContainText('Сидорова А.П.');
  await expect(page.getByRole('main')).toContainText('20 мая 2026');
  // Файл бланка — за кнопкой «Файлы (1)» в шапке записи.
  await page.getByRole('button', { name: 'Файлы' }).click();
  await expect(page.getByText('blank.pdf').first()).toBeVisible();

  // Распознавание выключено — фоновых задач нет.
  const tray = await (await api.get('/api/jobs/active-summary')).json();
  expect(tray.extraction.total).toBe(0);
});

test('приём врача из формы: файл без распознавания → в списке приёмов', async ({ page }) => {
  const { tgId, api } = await newUser('Формин', 'Фома');

  await openAs(page, tgId, '/health/visits');
  await page.getByRole('main').getByRole('button', { name: 'Добавить приём' }).first().click();
  await expect(page).toHaveURL(/\/health\/visits\/new/);
  await fillRecordForm(page, { file: 'conclusion.pdf', date: '2026-04-11', doctor: 'Терапевт Смирнова', note: 'ОРВИ, контроль через неделю' });
  await page.getByRole('main').getByRole('button', { name: 'Добавить приём', exact: true }).click();

  await expect(page).toHaveURL(/\/health\/visits$/);
  await expect(page.getByText(/1 приём ·/).first()).toBeVisible();
  await expect(page.getByText(/11 апреля 2026/).first()).toBeVisible();
  await expect(page.getByText('Врач: Терапевт Смирнова')).toBeVisible();

  await page.getByRole('button', { name: 'Открыть' }).click();
  await expect(page).toHaveURL(/\/health\/visits\/[0-9a-f-]{36}$/);
  await expect(page.getByText('ОРВИ, контроль через неделю')).toBeVisible();

  const tray = await (await api.get('/api/jobs/active-summary')).json();
  expect(tray.extraction.total).toBe(0);
});
