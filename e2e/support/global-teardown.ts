import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import { MINIO_CONTAINER, PG_CONTAINER, STATE_FILE } from './env';

export default async function globalTeardown(): Promise<void> {
  // При E2E_REUSE=1 стек оставляем жить для следующего запуска (остановить: E2E_STOP=1 или вручную).
  if (process.env.E2E_REUSE === '1' && process.env.E2E_STOP !== '1') return;

  try {
    const { apiPid } = JSON.parse(fs.readFileSync(STATE_FILE, 'utf8')) as { apiPid?: number };
    if (apiPid) {
      if (process.platform === 'win32') {
        // /T — всё дерево (dotnet run порождает дочерний процесс хоста).
        spawnSync('taskkill', ['/PID', String(apiPid), '/T', '/F'], { stdio: 'ignore' });
      } else {
        try { process.kill(-apiPid, 'SIGTERM'); } catch { /* уже остановлен */ }
      }
    }
  } catch {
    // state-файла нет — нечего останавливать
  }

  spawnSync('docker', ['rm', '-f', PG_CONTAINER, MINIO_CONTAINER], { stdio: 'ignore' });
}
