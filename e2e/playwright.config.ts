import { defineConfig, devices } from '@playwright/test';
import { API_URL } from './support/env';

/**
 * Сквозные сценарии FamilyHub. Против РЕАЛЬНОГО стека, который поднимает global-setup:
 * Postgres + MinIO в docker, настоящий API (dotnet run, Development) и dev-сборка Angular из
 * src/FamilyHub.Api/wwwroot (API сам раздаёт SPA — один origin, без прокси).
 *
 * Вход — через dev-заголовок (?devTgId=…): в dev-сборке фронт кладёт его в localStorage и шлёт
 * X-Dev-TelegramId, сервер (DevAuthenticationHandler) заводит пользователя сам. Это НЕ проверяет
 * PWA-вход по email+паролю (ему нужен OTP с почты) — только то, что происходит после входа.
 *
 * LM Studio направлен на закрытый порт: ИИ «недоступен» — заодно проверяется сценарий «ждём ИИ».
 */
export default defineConfig({
  testDir: './tests',
  // Один воркер: общий стек и общая БД, сценарии независимы по данным (у каждого свой пользователь),
  // но последовательный прогон проще отлаживать и не упирается в единственный воркер Hangfire.
  workers: 1,
  fullyParallel: false,
  retries: 0,
  timeout: 45_000,
  expect: { timeout: 10_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  globalSetup: './support/global-setup.ts',
  globalTeardown: './support/global-teardown.ts',
  use: {
    baseURL: API_URL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    locale: 'ru-RU',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } }, testIgnore: /mobile/ },
    { name: 'mobile', use: { ...devices['Pixel 7'] }, testMatch: /mobile/ },
  ],
});
