import { test, expect } from '@playwright/test';
import { createFamily, newUser, openAs } from '../support/api';

test('аптечка: список аптечек, открытая аптечка и просрочка на Главной', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  const fam = await createFamily(api, 'Семья Орловых');
  const kit = await (await api.post(`/api/families/${fam}/medkits`, { data: { name: 'Домашняя' } })).json();
  await api.post(`/api/medkits/${kit.id}/medications`, {
    data: { name: 'Аспирин 500', expiryDate: '2020-01-01', data: { quantity: '10' } },
  });

  // Главная: блок «Требует внимания» видит просроченное лекарство.
  await openAs(page, tgId, '/home');
  await expect(page.getByText('Просрочено 1 лекарство')).toBeVisible();
  await expect(page.getByText(/Аспирин 500 просрочен/)).toBeVisible();

  await openAs(page, tgId, '/health/medications');
  await expect(page.getByText('Домашняя').first()).toBeVisible();

  await openAs(page, tgId, `/health/medications/${kit.id}`);
  await expect(page.getByText('1 медикамент')).toBeVisible();
  await expect(page.getByText('Аспирин 500').first()).toBeVisible();
});

test('аптечка: добавление аптечки и лекарства через интерфейс', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  await createFamily(api, 'Семья Орловых');

  await openAs(page, tgId, '/health/medications');
  await page.getByRole('button', { name: /Добавить аптечку/ }).click();
  await page.getByPlaceholder('Название аптечки').fill('Дорожная');
  await page.getByRole('button', { name: 'Добавить', exact: true }).click();
  await expect(page.getByText('Дорожная').first()).toBeVisible();

  await page.getByText('Дорожная').first().click();
  await page.locator('.seg-opt', { hasText: 'Добавить' }).click(); // вкладка «Добавить» внутри аптечки
  await page.getByPlaceholder('Название препарата').fill('Ибупрофен 200');
  await page.getByPlaceholder('Количество').fill('20');
  await page.getByRole('button', { name: 'Добавить', exact: true }).last().click();
  await expect(page.getByText('Ибупрофен 200').first()).toBeVisible();
  await expect(page.getByText(/кол-во: 20/)).toBeVisible();
});
