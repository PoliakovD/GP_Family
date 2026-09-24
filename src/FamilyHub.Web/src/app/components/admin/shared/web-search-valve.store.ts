import { Injectable, inject, signal } from '@angular/core';
import { AdminApiService, WebSearchValve } from '../../../services/admin-api.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { ToastService } from '../../../shared/toast/toast.service';

/**
 * Состояние вентиля платного поиска (ADR-0005 §9) — одно на всю админку. Закрытие не отменяет
 * задачи обогащения, а откладывает их (EnrichmentJobStatus.Deferred); открытие возобновляет их
 * автоматически (DeferredEnrichmentReleaseJob на бэкенде).
 *
 * Переключается ТОЛЬКО на странице «Настройки → Веб-поиск» (`toggle()`), в остальных местах
 * (Требует внимания, Прогрев, Журнал вызовов) показывается баннер со ссылкой туда — раньше
 * вентиль переключался в трёх разных местах тремя разными способами.
 */
@Injectable({ providedIn: 'root' })
export class WebSearchValveStore {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly valve = signal<WebSearchValve | null>(null);
  readonly busy = signal(false);
  /** true — последняя загрузка не удалась (страница настроек показывает inline-ошибку, баннеры молчат). */
  readonly loadFailed = signal(false);

  async load(): Promise<void> {
    try {
      this.valve.set(await this.api.getWebSearchValve());
      this.loadFailed.set(false);
    } catch {
      this.loadFailed.set(true);
    }
  }

  /** Переключает вентиль после подтверждения (в обе стороны: остановка откладывает задачи,
   * включение запускает накопленное — оба действия глобальные и стоят денег). Возвращает true,
   * если состояние действительно изменилось. */
  async toggle(): Promise<boolean> {
    const current = this.valve();
    const nextPaused = !(current?.isPaused ?? false);

    const ok = await this.confirm.confirm({
      title: nextPaused ? 'Остановить платный поиск?' : 'Включить платный поиск?',
      message: nextPaused
        ? 'Новые платные вызовы (обогащение справочника, прогрев) будут откладываться до включения — уже накопленное не потеряется.'
        : 'Отложенные задачи обогащения и приостановленный прогрев возобновятся автоматически.',
      confirmText: nextPaused ? 'Остановить' : 'Включить',
      danger: nextPaused,
    });
    if (!ok) return false;

    this.busy.set(true);
    try {
      await this.api.setWebSearchValve(nextPaused, current?.note ?? null);
      await this.load();
      this.toast.success(nextPaused ? 'Платный поиск остановлен.' : 'Платный поиск включён.');
      return true;
    } catch {
      this.toast.error('Не удалось переключить вентиль поиска.');
      return false;
    } finally {
      this.busy.set(false);
    }
  }
}
