import { test, expect } from '@playwright/test';

test('гость без сессии попадает на вход, а не в приложение', async ({ page }) => {
  await page.goto('/home');
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByRole('textbox').first()).toBeVisible();
  // Нижней панели/меню приложения гостю не показывается.
  await expect(page.locator('nav.tab-bar')).toHaveCount(0);
});
