import * as path from 'node:path';

/** Порты выбраны заведомо «чужими» (не 5432/5433/5276 dev-стека), чтобы e2e не мешал локальной работе. */
export const API_PORT = 5290;
export const PG_PORT = 54329;
export const MINIO_PORT = 54339;
export const API_URL = `http://127.0.0.1:${API_PORT}`;

export const PG_CONTAINER = 'fh-e2e-postgres';
export const MINIO_CONTAINER = 'fh-e2e-minio';

export const POSTGRES_IMAGE = 'postgres:16-alpine';
// Тот же образ, что в интеграционных тестах (tests/FamilyHub.IntegrationTests/TestImages.cs).
export const MINIO_IMAGE = 'ghcr.io/poliakovd/gp_family-minio:RELEASE.2025-10-15T17-29-55Z';

export const REPO_ROOT = path.resolve(__dirname, '..', '..');
export const WEB_DIR = path.join(REPO_ROOT, 'src', 'FamilyHub.Web');
export const API_PROJECT = path.join(REPO_ROOT, 'src', 'FamilyHub.Api');
export const ARTIFACTS_DIR = path.join(REPO_ROOT, 'e2e', '.artifacts');
export const STATE_FILE = path.join(ARTIFACTS_DIR, 'state.json');

/**
 * Секреты только для e2e (не для прода). MasterKey — тот же design-time ключ, что у миграций
 * (DesignTimeDbContextFactory.DevMasterKey), JWT/подписи — фиксированные значения из тестовой фабрики.
 */
export const API_ENV: Record<string, string> = {
  ASPNETCORE_ENVIRONMENT: 'Development',
  ASPNETCORE_URLS: API_URL,
  ConnectionStrings__Postgres: `Host=127.0.0.1;Port=${PG_PORT};Database=familyhub_e2e;Username=postgres;Password=postgres`,
  Minio__Endpoint: `127.0.0.1:${MINIO_PORT}`,
  Minio__AccessKey: 'minioadmin',
  Minio__SecretKey: 'minioadmin',
  Minio__UseSsl: 'false',
  Encryption__MasterKey: 'MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=',
  Jwt__SigningKey: 'ZGV2LWp3dC1zaWduaW5nLWtleS0zMi1ieXRlcy1va2s=',
  Attachments__DownloadSigningKey: 'dev-attachment-download-signing-key',
  DevTools__DevAuthEnabled: 'true',
  DevTools__DevEndpointsEnabled: 'true',
  Telegram__BotToken: '',
  // Заведомо закрытый порт: ИИ «недоступен» (см. playwright.config.ts).
  LmStudio__BaseUrl: 'http://127.0.0.1:1',
  Messaging__Kafka__Enabled: 'false',
  // Все сценарии идут с одного IP (127.0.0.1) — дефолтные 10 запросов/мин на /api/auth/* дают 429 (как в
  // интеграционных тестах, FamilyHubWebFactory).
  RateLimiting__AuthPermitLimit: '100000',
  RateLimiting__CodePermitLimit: '100000',
  RateLimiting__RedeemPermitLimit: '100000',
};
