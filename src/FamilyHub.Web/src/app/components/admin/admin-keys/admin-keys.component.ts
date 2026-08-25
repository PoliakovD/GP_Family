import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { AdminApiService, AdminKeyRings, AdminSecurityStats, RotationStatus } from '../../../services/admin-api.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';

const POLL_INTERVAL_MS = 2000;

/**
 * Вкладка «Ключи» (ADR-0009): связки Encryption/Jwt/Attachments + управление ротацией мастер-
 * ключа шифрования. Поллинг статуса, пока прогон Running — тот же приём, что live-обновления
 * в других частях приложения (без WebSocket/SSE, дешёвый интервал на редко посещаемой странице).
 */
@Component({
  selector: 'app-admin-keys',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './admin-keys.component.html',
})
export class AdminKeysComponent implements OnInit, OnDestroy {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly rings = signal<AdminKeyRings | null>(null);
  readonly security = signal<AdminSecurityStats | null>(null);
  readonly rotation = signal<RotationStatus | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  private pollTimer?: ReturnType<typeof setTimeout>;

  ngOnInit(): void {
    void this.loadAll();
  }

  ngOnDestroy(): void {
    clearTimeout(this.pollTimer);
  }

  async loadAll(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const [rings, security, rotation] = await Promise.all([
        this.api.getKeyRings(), this.api.getSecurityStats(), this.api.getRotationStatus(),
      ]);
      this.rings.set(rings);
      this.security.set(security);
      this.rotation.set(rotation);
      this.schedulePollIfRunning();
    } catch {
      this.error.set('Не удалось загрузить данные о ключах.');
    } finally {
      this.loading.set(false);
    }
  }

  private schedulePollIfRunning(): void {
    clearTimeout(this.pollTimer);
    if (this.rotation()?.status !== 'Running') return;

    this.pollTimer = setTimeout(async () => {
      try {
        const status = await this.api.getRotationStatus();
        const wasRunning = this.rotation()?.status === 'Running';
        this.rotation.set(status);
        if (wasRunning && status.status !== 'Running') {
          this.toast.success('Перешифровка завершена.');
          // Распределение по ключам изменилось — освежаем его вместе со связками.
          this.rings.set(await this.api.getKeyRings());
          this.security.set(await this.api.getSecurityStats());
        }
      } catch {
        // Транзиентная ошибка поллинга — не считаем прогон завершённым, просто попробуем снова.
      }
      this.schedulePollIfRunning();
    }, POLL_INTERVAL_MS);
  }

  async startRotation(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Перешифровать данные?',
      message: 'Все поля и вложения, зашифрованные отставным ключом, будут перезаписаны активным. ' +
        'Операция фоновая, может занять время в зависимости от объёма данных — панель можно закрыть, прогон продолжится.',
      confirmText: 'Перешифровать',
      danger: false,
    });
    if (!ok) return;

    this.busy.set(true);
    try {
      await this.api.startRotation();
      this.toast.success('Перешифровка запущена.');
      this.rotation.set(await this.api.getRotationStatus());
      this.schedulePollIfRunning();
    } catch (e) {
      this.toast.error(
        e instanceof HttpErrorResponse && e.status === 409
          ? 'Нечего перешифровывать — активный ключ единственный в связке.'
          : 'Не удалось запустить перешифровку.',
      );
    } finally {
      this.busy.set(false);
    }
  }

  async cancelRotation(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Остановить перешифровку?',
      message: 'Прогон остановится на ближайшей завершённой странице. Уже перешифрованные данные ' +
        'останутся на новом ключе — продолжить можно будет позже той же кнопкой.',
      confirmText: 'Остановить',
      danger: true,
    });
    if (!ok) return;

    this.busy.set(true);
    try {
      await this.api.cancelRotation();
      this.toast.success('Остановка запрошена.');
      this.rotation.set(await this.api.getRotationStatus());
    } catch {
      this.toast.error('Не удалось остановить перешифровку.');
    } finally {
      this.busy.set(false);
    }
  }

  progressPercent(processed: number, total: number): number {
    return total === 0 ? 100 : Math.round((processed / total) * 100);
  }
}
