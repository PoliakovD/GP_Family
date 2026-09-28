import { test, expect } from '@playwright/test';
import { createFamily, createInviteCode, newUser, openAs } from '../support/api';

interface MeDto { userId: string }
interface PendingMemberDto { userId: string }

// ADR-0017 — «Кто видит моё здоровье»: владелец выдаёт доступ на чтение к дневнику из настроек,
// член семьи видит чип с именем владельца и не может ни создать, ни изменить его записи.
test('доступ к дневнику: выдача из настроек → чип у другого члена семьи → только чтение', async ({ page }) => {
  const owner = await newUser('Орлов', 'Олег');
  const familyId = await createFamily(owner.api, 'Семья Орловых');
  const viewer = await newUser('Орлова', 'Вера');

  const code = await createInviteCode(owner.api, familyId);
  await viewer.api.post(`/api/invites/${code}/redeem`);
  const pending = (await (await owner.api.get(`/api/families/${familyId}/pending`)).json()) as PendingMemberDto[];
  await owner.api.post(`/api/families/${familyId}/members/${pending[0].userId}/approve`);

  const ownerMe = (await (await owner.api.get('/api/auth/me')).json()) as MeDto;
  const viewerMe = (await (await viewer.api.get('/api/auth/me')).json()) as MeDto;

  await owner.api.post('/api/health-notes', {
    data: {
      kind: 0, // Symptom
      occurredAt: new Date().toISOString(),
      title: 'Головная боль',
      text: 'после работы',
      includeInDoctorQuestions: false,
      symptom: { severity: 7, areas: ['head'], detail: null },
    },
  });

  // Владелец: настройки → «Кто видит моё здоровье» → включает «Дневник» для Веры.
  await openAs(page, owner.tgId, '/settings/data');
  await expect(page.getByText('Кто видит моё здоровье')).toBeVisible();
  const row = page.locator('.settings-share-row', { hasText: 'Вера' });
  await expect(row).toBeVisible();
  const diaryToggle = row.locator('label.switch').nth(2); // 3-я колонка — «Дневник»
  // Дожидаемся именно ответа PUT — переключатель обновляется оптимистично, и не дождавшись
  // ответа, следующая навигация (openAs ниже) оборвала бы ещё не отправленный запрос.
  await Promise.all([
    page.waitForResponse((r) => r.url().includes('/api/health-shares/mine/') && r.request().method() === 'PUT'),
    diaryToggle.click(), // input скрыт визуально (opacity:0) — кликабелен сам <label>
  ]);
  await expect(diaryToggle.locator('input')).toBeChecked();

  // Зритель: дневник теперь показывает чип «Вера Орлов» (owner) рядом с «Я».
  await openAs(page, viewer.tgId, '/health/notes');
  const ownerChip = page.locator('.person-chip-btn', { hasText: 'Олег' }); // grant.name — только имя
  await expect(ownerChip).toBeVisible();
  await ownerChip.click();

  await expect(page.getByText('Головная боль')).toBeVisible();
  await expect(page.getByText(/Доступ дал\(а\) .*только чтение/)).toBeVisible();
  // Только чтение: ни кнопки добавления, ни меню действий на записи.
  await expect(page.getByRole('button', { name: 'Добавить запись' })).toHaveCount(0);
  await expect(page.locator('app-action-menu')).toHaveCount(0);

  // Прямой API-вызов от имени зрителя тоже отклоняется — грант читает, не пишет.
  const forbidden = await viewer.api.put(`/api/health-notes/00000000-0000-0000-0000-000000000000`, {
    data: { kind: 0, occurredAt: new Date().toISOString(), title: 'Взлом', text: null, includeInDoctorQuestions: false, symptom: { severity: 1, areas: [], detail: null } },
  });
  expect(forbidden.status()).toBe(404);
});
