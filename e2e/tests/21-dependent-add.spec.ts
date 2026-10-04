import { test, expect } from '@playwright/test';
import { createFamily, newUser, openAs } from '../support/api';

/** Дата рождения в 2018 году через `days` дней от сегодняшнего (в формате input[type=date]). */
function birthdayInDays(days: number): string {
  const d = new Date();
  d.setDate(d.getDate() + days);
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const dd = String(d.getDate()).padStart(2, '0');
  return `2018-${mm}-${dd}`;
}

test('подопечный из формы: проверка полей, добавление и ближайший день рождения', async ({ page }) => {
  const { tgId, api } = await newUser('Семейный', 'Степан');
  const fam = await createFamily(api, 'Семья Семейных');

  await openAs(page, tgId, `/families/${fam}?tab=dependents`);
  const main = page.getByRole('main');
  await expect(main.getByText(/Пока никого не добавили/)).toBeVisible();

  // Пустая форма — подсказка, а не запрос на сервер.
  await main.getByRole('button', { name: 'Добавить', exact: true }).click();
  await expect(main.getByText('Укажите фамилию.')).toBeVisible();

  await main.getByLabel('Фамилия').fill('Семейный');
  await main.getByLabel('Имя', { exact: false }).first().fill('Тимофей');
  await main.getByLabel('Пол').selectOption({ label: 'Мужской' });
  await main.getByLabel('Дата рождения').fill(birthdayInDays(5));
  await main.getByRole('button', { name: 'Добавить', exact: true }).click();

  await expect(main.getByText(/Пока никого не добавили/)).toHaveCount(0);
  await expect(main.getByText(/Тимофей/).first()).toBeVisible();
  await expect(main.getByText('Укажите фамилию.')).toHaveCount(0);

  const deps = (await (await api.get(`/api/families/${fam}/dependents`)).json()) as { firstName: string; isPet: boolean }[];
  expect(deps.map((d) => d.firstName)).toEqual(['Тимофей']);
  expect(deps[0].isPet).toBe(false);

  // День рождения через 5 дней — во вкладке семьи и на общем экране дней рождения.
  await main.getByText('Дни рождения', { exact: true }).click();
  await expect(main.getByText(/Тимофей/).first()).toBeVisible();

  await openAs(page, tgId, '/birthdays');
  await expect(page.getByRole('main').getByText(/Тимофей/).first()).toBeVisible();
});

test('питомец из формы: «Это питомец» — кличка, вид и пол самец/самка вместо ФИО', async ({ page }) => {
  const { tgId, api } = await newUser('Семейный', 'Савва');
  const fam = await createFamily(api, 'Семья Семейных');

  await openAs(page, tgId, `/families/${fam}?tab=dependents`);
  const main = page.getByRole('main');
  await main.getByText('Это питомец', { exact: true }).click();
  await expect(main.getByLabel('Фамилия')).toHaveCount(0);
  await main.getByLabel('Кличка').fill('Мурка');
  await main.getByLabel('Вид животного').fill('кошка');

  // Пол обязателен и для питомца.
  await main.getByRole('button', { name: 'Добавить', exact: true }).click();
  await expect(main.getByText('Выберите пол.')).toBeVisible();
  await main.getByLabel('Пол').selectOption({ label: 'Самка' });
  await main.getByRole('button', { name: 'Добавить', exact: true }).click();

  await expect(main.getByText('Мурка').first()).toBeVisible();
  const deps = (await (await api.get(`/api/families/${fam}/dependents`)).json()) as { firstName: string; isPet: boolean }[];
  expect(deps).toHaveLength(1);
  expect(deps[0]).toMatchObject({ firstName: 'Мурка', isPet: true, petSpecies: 'кошка' });
});
