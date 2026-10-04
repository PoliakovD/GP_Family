import { test, expect, request as pwRequest } from '@playwright/test';
import { API_URL } from '../support/env';

/**
 * Уже зарегистрированный PWA-пользователь: неверный пароль → понятная ошибка, «Забыли пароль?» с кодом
 * из письма (GET /dev/last-otp, как в 17-pwa-email-login) → вход с новым паролем → «Выйти» в «Безопасности».
 * Регистрация — через API: её экран уже проверяет 17-pwa-email-login.
 */
test('сброс пароля: неверный пароль, код с почты, вход с новым паролем и выход', async ({ page }) => {
  const unique = Date.now().toString(36);
  const email = `reset-${unique}@example.com`;
  const oldPassword = 'OldPassw0rd!';
  const newPassword = 'NewPassw0rd!';

  const anon = await pwRequest.newContext({ baseURL: API_URL });

  expect((await anon.post('/api/auth/register/start', { data: { email } })).ok()).toBeTruthy();
  const registered = await anon.post('/api/auth/register/confirm', {
    data: {
      email, code: await lastOtpFor(email), password: oldPassword, username: `rst${unique}`.slice(0, 20),
      lastName: 'Сбросов', firstName: 'Семён', middleName: null, birthDate: '1985-03-02', gender: 0,
    },
  });
  expect(registered.ok(), `register/confirm: HTTP ${registered.status()} ${await registered.text()}`).toBeTruthy();
  // Согласие ПДн фронт принимает сразу после кода — повторяем то же через сессию из регистрации.
  const consent = (await (await anon.get('/api/consents/current')).json()) as { version: string };
  expect((await anon.post('/api/consents/accept', { data: { version: consent.version } })).ok()).toBeTruthy();
  await anon.dispose();

  await page.goto('/login');
  await page.getByRole('region', { name: 'Об использовании cookie' }).getByRole('button', { name: 'Понятно' }).click();

  // Неверный пароль — ошибка на форме, остаёмся на входе.
  await page.locator('#login-email').fill(email);
  await page.locator('#login-password').fill('WrongPassw0rd!');
  await page.getByRole('button', { name: 'Войти' }).click();
  await expect(page.getByText('Неверный email или пароль.')).toBeVisible();
  await expect(page).toHaveURL(/\/login/);

  // «Забыли пароль?» → код → новый пароль: сразу в приложение.
  await page.getByRole('button', { name: 'Забыли пароль?' }).click();
  await page.locator('#reset-email').fill(email);
  await page.getByRole('button', { name: 'Отправить код' }).click();
  await expect(page.locator('#reset-code')).toBeVisible();
  await page.locator('#reset-code').fill(await lastOtpFor(email));
  await page.locator('#reset-new-password').fill(newPassword);
  await expect(page.getByText('Подходит')).toBeVisible();
  await page.getByRole('button', { name: 'Сохранить новый пароль' }).click();
  await expect(page).toHaveURL(/\/home/);
  await expect(page.getByRole('heading', { name: /Здравствуйте, Семён/ })).toBeVisible();

  // Выход из «Безопасности» → экран входа; приложение без сессии снова уводит на вход.
  await page.goto('/settings/security');
  await page.getByRole('main').getByRole('button', { name: 'Выйти', exact: true }).click();
  await expect(page).toHaveURL(/\/login/);
  await page.goto('/home');
  await expect(page).toHaveURL(/\/login/);

  // Старый пароль больше не подходит, новый — подходит.
  await page.locator('#login-email').fill(email);
  await page.locator('#login-password').fill(oldPassword);
  await page.getByRole('button', { name: 'Войти' }).click();
  await expect(page.getByText('Неверный email или пароль.')).toBeVisible();
  await page.locator('#login-password').fill(newPassword);
  await page.getByRole('button', { name: 'Войти' }).click();
  await expect(page).toHaveURL(/\/home/);
});

async function lastOtpFor(email: string): Promise<string> {
  const dev = await pwRequest.newContext({ baseURL: API_URL });
  try {
    const res = await dev.get('/dev/last-otp', { params: { email } });
    expect(res.ok(), `GET /dev/last-otp: HTTP ${res.status()}`).toBeTruthy();
    const { code } = (await res.json()) as { code: string };
    expect(code).toMatch(/^\d{6}$/);
    return code;
  } finally {
    await dev.dispose();
  }
}
