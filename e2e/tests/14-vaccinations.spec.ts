import { test, expect } from '@playwright/test';
import { newUser, createFamily, openAs } from '../support/api';

// Прививки (ADR-0016): график вычисляется бэкендом по каталогу нацкалендаря и дате рождения —
// сценарии подбирают возраст так, чтобы конкретный пункт графика оказался в предсказуемом статусе,
// как и юнит-тесты калькулятора (VaccinationScheduleCalculatorTests).

test('прививки: обзор показывает грипп «скоро» для взрослого и отмечается сделанным', async ({ page }) => {
  // onboard() ставит дату рождения 1990-05-04 — сентябрь/октябрь всегда сезон гриппа.
  const { tgId } = await newUser('Орлов', 'Олег');

  await openAs(page, tgId, '/health/vaccinations');
  await expect(page.getByText('Стоит запланировать')).toBeVisible();
  const card = page.locator('.vo-card', { hasText: 'Грипп' });
  await expect(card).toBeVisible();

  await card.getByRole('button', { name: 'Отметить сделанной' }).click();
  await expect(page.getByRole('heading', { name: 'Прививка сохранена' })).toBeVisible();
  await page.getByRole('button', { name: 'Готово' }).click();

  await expect(page.locator('.vo-card', { hasText: 'Грипп' })).toHaveCount(0);
});

test('прививки: график ребёнка — ревакцинация ККП «скоро», отметка по календарю сохраняется', async ({ page }) => {
  const { tgId, api } = await newUser('Орлов', 'Олег');
  const familyId = await createFamily(api, 'Семья Орловых');

  // Ребёнку только что исполнилось 6 лет и 10 дней — ревакцинация ККП (6-7 лет) уже в окне.
  const birth = new Date();
  birth.setFullYear(birth.getFullYear() - 6);
  birth.setDate(birth.getDate() - 10);
  const dep = await api.post(`/api/families/${familyId}/dependents`, {
    data: {
      firstName: 'Артём', lastName: null, middleName: null, gender: 0,
      birthDate: birth.toISOString().slice(0, 10), isPet: false, petSpecies: null,
    },
  });
  expect(dep.status()).toBe(201);
  const dependentId = (await dep.json()).id as string;

  await openAs(page, tgId, '/health/vaccinations');
  await expect(page.locator('.vo-card', { hasText: 'Корь' })).toBeVisible();

  await page.getByRole('link', { name: 'Артём' }).click();
  await expect(page).toHaveURL(new RegExp(`/health/vaccinations/people/dependent/${dependentId}$`));
  await expect(page.getByRole('heading', { name: 'Артём' })).toBeVisible();

  await page.getByRole('button', { name: 'Добавить', exact: true }).click();
  await page.getByRole('button', { name: 'Отметить по календарю' }).click();

  // БЦЖ — «сделано» без даты; Корь (ККП) — «не помню».
  const bcgRow = page.locator('.va-cal-row', { hasText: 'Туберкулёз' }).first();
  await bcgRow.locator('.va-cal-check').click();
  const mmrRow = page.locator('.va-cal-row', { hasText: 'Корь' }).first();
  await mmrRow.getByRole('button', { name: 'не помню' }).click();

  await page.getByRole('button', { name: /Сохранить \d+/ }).click();
  await expect(page.getByText(/Отмечено \d+/)).toBeVisible();

  // Модалка закрылась, график перечитан — БЦЖ теперь «Сделано» (дата не указывалась — «—»).
  const bcgRowOnPage = page.locator('.vx-dose', { hasText: 'Туберкулёз (БЦЖ)' }).first();
  await expect(bcgRowOnPage).toBeVisible();
  await expect(bcgRowOnPage.locator('.vx-dose-date')).toHaveText('—');
});
