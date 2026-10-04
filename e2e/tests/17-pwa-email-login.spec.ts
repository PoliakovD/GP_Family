import { test, expect, request as pwRequest } from '@playwright/test';
import { API_URL } from '../support/env';

/**
 * PWA-вход по email+паролю — единственный путь входа, который 05-ai-unavailable.spec.ts и все
 * остальные сценарии сознательно НЕ проверяют (они идут через ?devTgId=, см. playwright.config.ts):
 * ему нужен код из письма, а в CI нет настоящего почтового ящика. GET /dev/last-otp (TECH_DEBT.md
 * #11) даёт то же самое, что CapturingEmailSender.LastCodeFor в интеграционных тестах — программный
 * доступ к коду без реальной почты, работает только пока DevTools:DevEndpointsEnabled=true (см.
 * env.ts) и ни один реальный email-провайдер не настроен (тестовый стек — оба условия выполнены).
 */
test('регистрация email+паролем: код с почты, вход, доступ в приложение', async ({ page }) => {
  const unique = Date.now().toString(36);
  const email = `pwa-${unique}@example.com`;
  const username = `pwa${unique}`.slice(0, 20);
  const password = 'Str0ngPassw0rd!';

  await page.goto('/login');
  // Баннер cookie перекрывает форму (fixed-позиционирование) — снять сразу, иначе поздние клики
  // по кнопкам формы (например «Отправить код на email») на него и попадут.
  await page.getByRole('region', { name: 'Об использовании cookie' }).getByRole('button', { name: 'Понятно' }).click();
  await page.getByRole('button', { name: 'Создать аккаунт' }).click();

  await page.locator('#reg-email').fill(email);
  await page.locator('#reg-username').fill(username);
  await expect(page.getByText('Свободен')).toBeVisible();
  await page.locator('#reg-last-name').fill('Тестов');
  await page.locator('#reg-first-name').fill('Тест');
  await page.locator('#reg-birth-date').fill('1990-05-04');
  // [ngValue] рендерит служебный value-атрибут (не литеральное "0"/"1") — выбираем по видимому тексту.
  await page.locator('#reg-gender').selectOption({ label: 'Мужской' });
  await page.locator('#reg-password').fill(password);
  await page.getByRole('checkbox').nth(0).check();
  await page.getByRole('checkbox').nth(1).check();
  await page.getByRole('checkbox').nth(2).check();

  const submit = page.getByRole('button', { name: 'Отправить код на email' });
  await expect(submit).toBeEnabled();
  await submit.click();

  await expect(page.locator('#reg-code')).toBeVisible();

  // Письмо ушло синхронно внутри запроса выше — код уже должен быть в перехвате к этому моменту,
  // без отдельного ожидания/поллинга.
  const dev = await pwRequest.newContext({ baseURL: API_URL });
  const otpResponse = await dev.get('/dev/last-otp', { params: { email } });
  expect(otpResponse.ok(), `GET /dev/last-otp: HTTP ${otpResponse.status()}`).toBeTruthy();
  const { code } = (await otpResponse.json()) as { code: string };
  expect(code).toMatch(/^\d{6}$/);

  await page.locator('#reg-code').fill(code);
  await page.getByRole('button', { name: 'Создать аккаунт' }).click();

  // Аккаунт создан профилем целиком, а согласие ПДн принято сразу после кода (см. confirmRegistration)
  // — сразу в приложение, ни один гард (auth/consent/profile) не должен увести в сторону.
  await expect(page).toHaveURL(/\/home/);
});
