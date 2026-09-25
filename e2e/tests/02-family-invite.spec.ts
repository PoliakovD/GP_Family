import { test, expect } from '@playwright/test';
import { createFamily, createInviteCode, newUser, openAs } from '../support/api';

test('приглашение: ссылка из «Кто в семье», заявка → «Принять» → участник в семье', async ({ page }) => {
  const admin = await newUser('Админов', 'Артём');
  const familyId = await createFamily(admin.api, 'Семья Админовых');

  await openAs(page, admin.tgId);
  await expect(page.getByRole('heading', { name: /Здравствуйте, Артём/ })).toBeVisible();

  // Админу семьи доступна кнопка «Пригласить по ссылке» — модалка создаёт одноразовую ссылку.
  await page.getByRole('button', { name: 'Пригласить по ссылке' }).click();
  await expect(page.getByText('Пригласить в семью')).toBeVisible();
  await page.getByRole('button', { name: 'Создать ссылку' }).click();
  await expect(page.locator('.invite-link')).toContainText('/join/');
  // У модалки две «Закрыть» (крестик с aria-label и текстовая кнопка) — берём текстовую.
  await page.locator('app-invite-modal button', { hasText: 'Закрыть' }).click();

  // Второй человек переходит по приглашению (через API) — у админа появляется заявка.
  const guest = await newUser('Иванова', 'Мария');
  const code = await createInviteCode(admin.api, familyId);
  const redeem = await guest.api.post(`/api/invites/${code}/redeem`);
  expect(redeem.ok()).toBeTruthy();

  await page.reload();
  await expect(page.getByText('Требует внимания')).toBeVisible();
  await expect(page.getByText(/Мария просит вступить в семью «Семья Админовых»/)).toBeVisible();

  await page.getByRole('button', { name: 'Принять' }).click();
  await expect(page.getByText('Заявка принята.')).toBeVisible();
  // После принятия заявка исчезает, участник в списке «Кто в семье».
  await expect(page.getByText(/просит вступить/)).toHaveCount(0);
  await expect(page.getByText(/Иванова Мария|Мария Иванова/).first()).toBeVisible();
});
