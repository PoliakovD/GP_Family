import { test, expect, type Page } from '@playwright/test';
import { createFamily, createInviteCode, newUser, openAs } from '../support/api';

interface PendingMemberDto { userId: string }

/** Сообщение в ленте чата по тексту. */
const message = (page: Page, text: string) => page.locator('.msg', { hasText: text });

// ADR-0019 — чат семьи: сообщение второго участника приходит без перезагрузки (SignalR), ответ с цитатой,
// реакция, бейдж непрочитанного в сайдбаре у того, кто чат не открыл.
test('чат семьи: realtime-доставка, ответ, реакция, бейдж непрочитанного', async ({ page, browser }) => {
  const admin = await newUser('Чатов', 'Антон');
  const familyId = await createFamily(admin.api, 'Семья Чатовых');
  const member = await newUser('Чатова', 'Мария');

  const code = await createInviteCode(admin.api, familyId);
  await member.api.post(`/api/invites/${code}/redeem`);
  const pending = (await (await admin.api.get(`/api/families/${familyId}/pending`)).json()) as PendingMemberDto[];
  await admin.api.post(`/api/families/${familyId}/members/${pending[0].userId}/approve`);

  // Админ открывает пустой чат.
  await openAs(page, admin.tgId, `/families/${familyId}?tab=chat`);
  await expect(page.getByText('Здесь пока тихо.')).toBeVisible();

  // Участница — в отдельном браузерном контексте (своё localStorage с devTgId).
  const memberContext = await browser.newContext();
  const memberPage = await memberContext.newPage();
  await openAs(memberPage, member.tgId, `/families/${familyId}?tab=chat`);
  await expect(memberPage.getByText('Здесь пока тихо.')).toBeVisible();

  await memberPage.getByLabel('Текст сообщения').fill('Купила лекарства бабушке');
  await memberPage.getByRole('button', { name: 'Отправить' }).click();
  await expect(message(memberPage, 'Купила лекарства бабушке')).toBeVisible();

  // У админа — без перезагрузки страницы.
  await expect(message(page, 'Купила лекарства бабушке')).toBeVisible({ timeout: 15_000 });
  await expect(message(page, 'Купила лекарства бабушке').locator('.msg-author')).toHaveText('Мария Чатова');

  // Ответ через меню сообщения — цитата видна у участницы.
  await message(page, 'Купила лекарства бабушке').hover();
  await message(page, 'Купила лекарства бабушке').getByRole('button', { name: 'Действия с сообщением' }).click();
  await page.getByRole('button', { name: 'Ответить' }).click();
  await page.getByLabel('Текст сообщения').fill('Спасибо!');
  await page.getByRole('button', { name: 'Отправить' }).click();

  const reply = message(memberPage, 'Спасибо!');
  await expect(reply).toBeVisible({ timeout: 15_000 });
  await expect(reply.locator('.reply-quote')).toContainText('Купила лекарства бабушке');

  // Реакция участницы на ответ — у админа появляется счётчик.
  await reply.hover();
  await reply.getByRole('button', { name: 'Действия с сообщением' }).click();
  await memberPage.getByRole('button', { name: 'Реакция' }).click();
  await reply.locator('.reaction-picker .reaction', { hasText: '👍' }).click();
  await expect(message(page, 'Спасибо!').locator('.reaction', { hasText: '👍' })).toContainText('1', { timeout: 15_000 });

  // Админ уходит из чата — новое сообщение участницы даёт бейдж «Семья» в сайдбаре.
  await page.goto(new URL('/home', page.url()).toString());
  const sent = await member.api.post(`/api/families/${familyId}/chat/messages`, { multipart: { text: 'Вы где?' } });
  expect(sent.status()).toBe(201);
  await expect(page.locator('.app-sidebar-link', { hasText: 'Семья' }).locator('.app-sidebar-badge')).toHaveText('1', { timeout: 15_000 });

  // Открыл чат — прочитано, бейдж исчез.
  await page.goto(new URL(`/families/${familyId}?tab=chat`, page.url()).toString());
  await expect(message(page, 'Вы где?')).toBeVisible();
  await expect(page.locator('.app-sidebar-link', { hasText: 'Семья' }).locator('.app-sidebar-badge')).toHaveCount(0, { timeout: 15_000 });

  await memberContext.close();
});
