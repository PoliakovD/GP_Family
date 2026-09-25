import { execFileSync, spawn, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import {
  API_ENV, API_PROJECT, API_URL, ARTIFACTS_DIR, MINIO_CONTAINER, MINIO_IMAGE, MINIO_PORT, PG_CONTAINER, PG_PORT,
  POSTGRES_IMAGE, STATE_FILE, WEB_DIR,
} from './env';

const isWin = process.platform === 'win32';

function docker(args: string[], opts: { ignoreError?: boolean } = {}): string {
  const r = spawnSync('docker', args, { encoding: 'utf8' });
  if (r.status !== 0 && !opts.ignoreError) {
    throw new Error(`docker ${args.join(' ')} завершился с кодом ${r.status}: ${r.stderr || r.stdout}`);
  }
  return (r.stdout ?? '').trim();
}

async function waitFor(what: string, probe: () => Promise<boolean> | boolean, timeoutMs: number, everyMs = 1000): Promise<void> {
  const started = Date.now();
  let lastError = '';
  while (Date.now() - started < timeoutMs) {
    try {
      if (await probe()) return;
    } catch (e) {
      lastError = String(e);
    }
    await new Promise((r) => setTimeout(r, everyMs));
  }
  throw new Error(`Не дождались: ${what} (${Math.round(timeoutMs / 1000)} с). ${lastError}`);
}

/** Сборка фронта в dev-конфигурации (isDevMode() → работает вход через ?devTgId=). E2E_SKIP_BUILD=1 — пропустить. */
function buildFrontend(): void {
  if (process.env.E2E_SKIP_BUILD === '1' && fs.existsSync(path.join(API_PROJECT, 'wwwroot', 'index.html'))) {
    console.log('[e2e] сборка фронта пропущена (E2E_SKIP_BUILD=1)');
    return;
  }
  console.log('[e2e] сборка фронта (development)…');
  // Сборка отдельно не тянет prod-оптимизации — быстрее, и нужна именно dev-конфигурация.
  // node + ng.js напрямую: без shell (на Windows npx.cmd требует shell:true, а это DEP0190).
  const ng = require.resolve('@angular/cli/bin/ng.js', { paths: [WEB_DIR] });
  execFileSync(process.execPath, [ng, 'build', '--configuration', 'development'], { cwd: WEB_DIR, stdio: 'inherit' });
}

function startContainers(): void {
  // Хвосты прошлого (упавшего) прогона.
  docker(['rm', '-f', PG_CONTAINER, MINIO_CONTAINER], { ignoreError: true });

  console.log('[e2e] запуск Postgres и MinIO…');
  docker([
    'run', '-d', '--rm', '--name', PG_CONTAINER, '-p', `${PG_PORT}:5432`,
    '-e', 'POSTGRES_PASSWORD=postgres', '-e', 'POSTGRES_DB=familyhub_e2e', POSTGRES_IMAGE,
  ]);
  docker([
    'run', '-d', '--rm', '--name', MINIO_CONTAINER, '-p', `${MINIO_PORT}:9000`,
    '-e', 'MINIO_ROOT_USER=minioadmin', '-e', 'MINIO_ROOT_PASSWORD=minioadmin', MINIO_IMAGE, 'server', '/data',
  ]);
}

export default async function globalSetup(): Promise<void> {
  fs.mkdirSync(ARTIFACTS_DIR, { recursive: true });

  // E2E_REUSE=1 — стек уже поднят прошлым запуском (быстрый цикл отладки): ничего не пересоздаём.
  if (process.env.E2E_REUSE === '1' && (await fetch(`${API_URL}/health/live`).then((r) => r.ok).catch(() => false))) {
    console.log('[e2e] переиспользуем уже запущенный стек');
    return;
  }

  buildFrontend();
  startContainers();

  await waitFor('Postgres', () => {
    const r = spawnSync('docker', ['exec', PG_CONTAINER, 'pg_isready', '-U', 'postgres', '-d', 'familyhub_e2e'], { encoding: 'utf8' });
    return r.status === 0;
  }, 60_000);
  await waitFor('MinIO', async () => {
    const res = await fetch(`http://127.0.0.1:${MINIO_PORT}/minio/health/live`);
    return res.ok;
  }, 60_000);

  console.log('[e2e] запуск API (dotnet run)…');
  const log = fs.openSync(path.join(ARTIFACTS_DIR, 'api.log'), 'w');
  const api = spawn('dotnet', ['run', '--project', API_PROJECT, '--no-launch-profile'], {
    env: { ...process.env, ...API_ENV },
    stdio: ['ignore', log, log],
    // На Windows нужен отдельный процесс без консоли; убиваем дерево в teardown.
    detached: !isWin,
    windowsHide: true,
  });
  if (!api.pid) throw new Error('Не удалось запустить dotnet run');

  fs.writeFileSync(STATE_FILE, JSON.stringify({ apiPid: api.pid }));

  // Миграции + старт Hangfire на пустой БД — с запасом.
  await waitFor('API /health/live', async () => {
    if (api.exitCode !== null) throw new Error(`API завершился (код ${api.exitCode}), см. e2e/.artifacts/api.log`);
    const res = await fetch(`${API_URL}/health/live`);
    return res.ok;
  }, 180_000, 1500);

  console.log('[e2e] стек готов:', API_URL);
}
