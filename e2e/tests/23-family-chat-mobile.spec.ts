import { test, expect } from '@playwright/test';
import { createFamily, createInviteCode, newUser, openAs } from '../support/api';

interface PendingMemberDto { userId: string }

// ADR-0019, мобильная раскладка: на сенсорном экране Enter — перенос строки (отправка — кнопкой),
// непрочитанное в чате — точка на табе «Семья».
test('чат семьи на телефоне: многострочное сообщение кнопкой, точка непрочитанного на табе', async ({ page }) => {
  const admin = await newUser('Мобилов', 'Матвей');
  const familyId = await createFamily(admin.api, 'Семья Мобиловых');
  const member = await newUser('Мобилова', 'Майя');
  const code = await createInviteCode(admin.api, familyId);
  await member.api.post(`/api/invites/${code}/redeem`);
  const pending = (await (await admin.api.get(`/api/families/${familyId}/pending`)).json()) as PendingMemberDto[];
  await admin.api.post(`/api/families/${familyId}/members/${pending[0].userId}/approve`);

  await openAs(page, admin.tgId, '/home');
  const familyTab = page.locator('.tab-bar button', { hasText: 'Семья' });
  await expect(familyTab.locator('.tab-bar-dot')).toHaveCount(0);

  const sent = await member.api.post(`/api/families/${familyId}/chat/messages`, { multipart: { text: 'Позвони, как сможешь' } });
  expect(sent.status()).toBe(201);
  await expect(familyTab.locator('.tab-bar-dot')).toBeVisible({ timeout: 15_000 });

  await page.goto(new URL(`/families/${familyId}?tab=chat`, page.url()).toString());
  await expect(page.locator('.msg', { hasText: 'Позвони, как сможешь' })).toBeVisible();
  await expect(familyTab.locator('.tab-bar-dot')).toHaveCount(0, { timeout: 15_000 });

  const input = page.getByLabel('Текст сообщения');
  await input.click();
  await input.pressSequentially('Перезвоню');
  await input.press('Enter');
  await input.pressSequentially('вечером');
  await expect(page.locator('.msg', { hasText: 'Перезвоню' })).toHaveCount(0); // Enter не отправил

  await page.getByRole('button', { name: 'Отправить' }).click();
  await expect(page.locator('.msg .msg-text', { hasText: 'Перезвоню' })).toHaveText('Перезвоню\nвечером');
});
