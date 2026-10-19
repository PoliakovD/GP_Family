import { test, expect } from '@playwright/test';

test('стек поднят: API отвечает, SPA раздаётся', async ({ request, page }) => {
  const live = await request.get('/health/live');
  expect(live.ok()).toBeTruthy();

  // Гость на «/» видит лендинг, «Войти» ведёт на форму входа.
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1 })).toContainText('Здоровье всей семьи');
  await page.getByRole('link', { name: 'Войти', exact: true }).first().click();
  await expect(page).toHaveURL(/\/login/);
});
