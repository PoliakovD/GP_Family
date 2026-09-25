import { test, expect } from '@playwright/test';
import { freshTgId, openAs } from '../support/api';

test('новый пользователь: согласие → профиль → Главная с приветствием', async ({ page }) => {
  const tgId = freshTgId();
  await openAs(page, tgId);

  // Согласие ПДн: кнопка неактивна, пока не отмечены оба чекбокса.
  await expect(page).toHaveURL(/\/consent/);
  const accept = page.getByRole('button', { name: 'Принять и продолжить' });
  await expect(accept).toBeDisabled();
  await page.getByRole('checkbox').first().check();
  await page.getByRole('checkbox').nth(1).check();
  await expect(accept).toBeEnabled();
  await accept.click();

  // Профиль.
  await expect(page).toHaveURL(/\/profile-setup/);
  await page.getByLabel('Фамилия').fill('Петров');
  await page.getByLabel('Имя', { exact: true }).fill('Пётр');
  await page.getByLabel('Дата рождения').fill('1990-05-04');
  await page.getByRole('button', { name: 'Продолжить' }).click();

  // Главная.
  await expect(page.getByRole('heading', { name: /Здравствуйте, Пётр/ })).toBeVisible();
});
