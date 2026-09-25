import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { AdminApiService, KbRebuildStatus } from '../../../services/admin-api.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { AdminStatusPipe } from '../shared/admin-status.pipe';

const REBUILD_POLL_INTERVAL_MS = 2000;

/**
 * «Операции → Пересборки»: разовые тяжёлые действия над справочником показателей. Раньше они были
 * разбросаны: пересборка — во вкладке «Обогащения», перепрогон норм — в «Пайплайн → Шаги», а
 * пакетное переобогащение вообще не имело кнопки. Все три стоят времени/денег и необратимы по
 * своей природе, поэтому каждое запускается только после подтверждения.
 *
 *  1. Полная пересборка справочника — с отслеживанием статуса (прогон длинный, со стадиями).
 *  2. Перепрогон норм показателей — фоновая задача без статуса (см. recomputeIndicatorFlags).
 *  3. Переобогащение показателей со старой схемой — фоновая задача без статуса.
 */
@Component({
    selector: 'app-admin-rebuilds',
    imports: [DatePipe, AdminStatusPipe],
    templateUrl: './admin-rebuilds.component.html'
})
export class AdminRebuildsComponent implements OnInit, OnDestroy {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly rebuild = signal<KbRebuildStatus | null>(null);
  readonly rebuildLoading = signal(true);
  readonly rebuildBusy = signal(false);
  readonly rebuildError = signal<string | null>(null);
  private pollTimer?: ReturnType<typeof setTimeout>;

  readonly recomputeBusy = signal(false);
  readonly reenrichBusy = signal(false);

  ngOnInit(): void {
    void this.loadRebuildStatus();
  }

  ngOnDestroy(): void {
    clearTimeout(this.pollTimer);
  }

  // --- Полная пересборка справочника (§4.2 плана) — поллинг статуса, пока прогон Running ---

  async loadRebuildStatus(): Promise<void> {
    this.rebuildLoading.set(true);
    this.rebuildError.set(null);
    try {
      this.rebuild.set(await this.api.getKbRebuildStatus());
      this.schedulePollIfRunning();
    } catch {
      this.rebuildError.set('Не удалось загрузить статус пересборки.');
    } finally {
      this.rebuildLoading.set(false);
    }
  }

  private schedulePollIfRunning(): void {
    clearTimeout(this.pollTimer);
    if (this.rebuild()?.status !== 'Running') return;

    this.pollTimer = setTimeout(async () => {
      try {
        const status = await this.api.getKbRebuildStatus();
        const wasRunning = this.rebuild()?.status === 'Running';
        this.rebuild.set(status);
        if (wasRunning && status.status !== 'Running') {
          this.toast[status.status === 'Completed' ? 'success' : 'error'](
            status.status === 'Completed' ? 'Пересборка справочника завершена.' : `Пересборка упала: ${status.lastError ?? 'см. логи'}`,
          );
        }
      } catch {
        // Транзиентная ошибка поллинга — не считаем прогон завершённым, просто попробуем снова.
      }
      this.schedulePollIfRunning();
    }, REBUILD_POLL_INTERVAL_MS);
  }

  async startRebuild(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Пересобрать справочник показателей?',
      message: 'Ключи показателей будут пересчитаны новым нормализатором, справочник анализов ' +
        'очищен и наполнен заново поверх уже оплаченного кэша поиска (новых внешних запросов не ' +
        'потребуется). Операция фоновая, панель можно закрыть — прогон продолжится.',
      confirmText: 'Пересобрать',
      danger: true,
    });
    if (!ok) return;

    this.rebuildBusy.set(true);
    try {
      await this.api.startKbRebuild();
      this.toast.success('Пересборка запущена.');
      this.rebuild.set(await this.api.getKbRebuildStatus());
      this.schedulePollIfRunning();
    } catch {
      this.toast.error('Не удалось запустить пересборку.');
    } finally {
      this.rebuildBusy.set(false);
    }
  }

  // --- Перепрогон норм ---

  /** Одноразовый перепрогон показателей, застрявших на Flag.Unknown ДО фикса каскада
   * IndicatorFlagCalculator (план "нормы из бланка") — RecomputeIndicatorFlagsBackfillJob, фон,
   * без прогресс-бара: задача разовая и не привязана ни к одной из четырёх таблиц задач конвейера,
   * поэтому статус не отслеживается — только тост "поставлено в очередь", результат смотреть в
   * логах Hangfire либо по факту позеленевших строк. */
  async recomputeIndicatorFlags(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Перепрогнать нормы показателей?',
      message: 'Все показатели, застрявшие на "нет данных" из-за старого бага (одностороннего "<47"/">47" или качественного результата типа "не обнаружено"), будут пересчитаны заново по уже сохранённым данным. Задача фоновая — результат не отображается здесь напрямую.',
      confirmText: 'Перепрогнать',
    });
    if (!ok) return;

    this.recomputeBusy.set(true);
    try {
      await this.api.recomputeIndicatorFlags();
      this.toast.success('Перепрогон поставлен в очередь.');
    } catch {
      this.toast.error('Не удалось поставить перепрогон в очередь.');
    } finally {
      this.recomputeBusy.set(false);
    }
  }

  // --- Переобогащение показателей со старой схемой ---

  /** LabAnalyteKbReenrichJob: первый батч ставится автоматически после миграции схемы, повторные
   * запуски — отсюда, пока строк со старой схемой не останется. Идемпотентно: если таких строк
   * нет, задача завершается сразу. */
  async reenrichLabAnalytes(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Переобогатить показатели со старой схемой?',
      message: 'За один запуск в очередь встанет до 25 показателей, сохранённых по устаревшей схеме. Они пройдут через обычный конвейер обогащения (работа ИИ); платный поиск — только для тех, у кого нет свежего кэша. Если таких показателей нет, задача завершится сразу. Задача фоновая — результат здесь не отображается.',
      confirmText: 'Запустить',
    });
    if (!ok) return;

    this.reenrichBusy.set(true);
    try {
      await this.api.reenrichLabAnalytesBatch();
      this.toast.success('Переобогащение поставлено в очередь.');
    } catch {
      this.toast.error('Не удалось поставить переобогащение в очередь.');
    } finally {
      this.reenrichBusy.set(false);
    }
  }
}
