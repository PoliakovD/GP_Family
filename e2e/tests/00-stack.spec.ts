import { test, expect } from '@playwright/test';

test('стек поднят: API отвечает, SPA раздаётся', async ({ request, page }) => {
  const live = await request.get('/health/live');
  expect(live.ok()).toBeTruthy();

  await page.goto('/');
  await expect(page).toHaveURL(/\/login/);
});
