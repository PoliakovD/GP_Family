import { test, expect } from '@playwright/test';
import { createFamily, newUser, openAs } from '../support/api';

test('семья: вкладки и подопечный видны на странице семьи', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  const fam = await createFamily(api, 'Семья Орловых');
  const dep = await api.post(`/api/families/${fam}/dependents`, {
    data: { firstName: 'Барсик', lastName: null, middleName: null, gender: 0, birthDate: null, isPet: true, petSpecies: 'кот' },
  });
  expect(dep.status()).toBe(201);

  await openAs(page, tgId, `/families/${fam}`);
  for (const tab of ['Участники', 'Аптечки', 'Дни рождения', 'Близкие и питомцы']) {
    await expect(page.getByText(tab, { exact: true }).first()).toBeVisible();
  }

  await openAs(page, tgId, `/families/${fam}?tab=dependents`);
  await expect(page.getByText('Барсик').first()).toBeVisible();
  await expect(page.getByText('кот').first()).toBeVisible();
});

test('семья: список семей показывает созданную семью', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await createFamily(api, 'Семья Орловых');

  await openAs(page, tgId, '/families');
  await expect(page.getByText('Семья Орловых').first()).toBeVisible();
});

test('дни рождения: экран открывается', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await createFamily(api, 'Семья Орловых');

  await openAs(page, tgId, '/birthdays');
  await expect(page).toHaveURL(/birthdays/);
  await expect(page.locator('main')).toBeVisible();
});
