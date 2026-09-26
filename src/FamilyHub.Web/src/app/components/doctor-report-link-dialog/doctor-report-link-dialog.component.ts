import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { ApiError, ApiService } from '../../services/api.service';
import { TelegramService } from '../../services/telegram.service';
import { DoctorReport, DoctorReportLinkStatus } from '../../models/types';
import { ModalComponent } from '../../shared/modal/modal.component';
import { ToastService } from '../../shared/toast/toast.service';
import { copyToClipboard } from '../../shared/util/clipboard';
import {
  DEFAULT_SHARE_DAYS, SHARE_DAY_OPTIONS, dayMonthFromIso, expiryPreview, reportUrl,
} from '../../shared/util/report-labels';
import { shareLink } from '../../shared/util/share-link';

/**
 * Диалог ссылки для врача. Два состояния: «выбрать срок» (создать / продлить / выпустить новую) и
 * «ссылка готова» (показать адрес текстом + скопировать/поделиться). `report === null` — закрыт.
 * Начальное состояние по данным отчёта: у отчёта с готовой активной ссылки, открытого сразу после
 * создания (`showResult`), — сразу «готова».
 */
@Component({
  selector: 'app-doctor-report-link-dialog',
  imports: [ModalComponent],
  template: `
    <app-modal [title]="title()" [open]="report() !== null" (closed)="closed.emit()">
      @if (ready(); as r) {
        <p class="muted mb-2">
          Ссылка работает до {{ expires(r) }}. Врач откроет отчёт без регистрации; отозвать доступ можно в любой момент.
        </p>
        <div class="link-box mb-2">{{ url(r) }}</div>
        <div class="d-flex gap-2 mb-2">
          <button type="button" class="btn btn-primary flex-grow-1" (click)="share(r)">Поделиться</button>
          <button type="button" class="btn btn-secondary btn-icon" title="Скопировать ссылку" aria-label="Скопировать ссылку" (click)="copy(r)">
            <i class="ph ph-copy" aria-hidden="true"></i>
          </button>
        </div>
        <div class="d-flex justify-content-end">
          <button type="button" class="btn btn-secondary btn-sm" (click)="closed.emit()">Готово</button>
        </div>
      } @else {
        <p class="muted mb-2">{{ intro() }}</p>
        <div class="seg mb-1" role="radiogroup" aria-label="Срок ссылки">
          @for (d of options; track d) {
            <button type="button" class="seg-opt dr-seg" role="radio" [class.active]="days() === d"
                    [attr.aria-checked]="days() === d" (click)="days.set(d)">{{ d }} дней</button>
          }
        </div>
        <p class="muted mb-3">Ссылка будет работать до {{ preview() }}.</p>
        @if (error(); as e) {
          <div class="alert-danger mb-2" role="alert">{{ e }}</div>
        }
        <div class="d-flex justify-content-end gap-2">
          <button type="button" class="btn btn-secondary btn-sm" (click)="closed.emit()">Отмена</button>
          <button type="button" class="btn btn-primary btn-sm" [disabled]="busy()" (click)="submit()">
            {{ extending() ? 'Продлить' : 'Создать ссылку' }}
          </button>
        </div>
      }
    </app-modal>
  `,
  styles: `
    .link-box {
      padding: 8px 12px;
      background: var(--color-paper);
      border: 1px solid var(--color-divider);
      border-radius: var(--radius-md);
      font-family: monospace;
      font-size: var(--font-size-meta);
      word-break: break-all;
      user-select: all;
    }
    .dr-seg { font: inherit; font-size: 0.7647rem; color: inherit; background: transparent; border: 0; }
    .dr-seg + .dr-seg { border-left: 1px solid var(--color-divider); }
    .dr-seg.active { color: var(--color-bg); background: var(--color-accent); }
  `,
})
export class DoctorReportLinkDialogComponent {
  private readonly api = inject(ApiService);
  private readonly tg = inject(TelegramService);
  private readonly toast = inject(ToastService);

  /** Отчёт, для которого выдаём ссылку; null — диалог закрыт. */
  readonly report = input<DoctorReport | null>(null);
  /** true — открыть сразу в состоянии «ссылка готова» (отчёт только что создан со ссылкой). */
  readonly showResult = input(false);
  /** Обновлённый отчёт после выдачи/продления ссылки. */
  readonly changed = output<DoctorReport>();
  readonly closed = output<void>();

  protected readonly options = SHARE_DAY_OPTIONS;
  protected readonly days = signal<number>(DEFAULT_SHARE_DAYS);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  /** Отчёт с готовой ссылкой — после успешного submit() либо сразу, если showResult. */
  private readonly issued = signal<DoctorReport | null>(null);

  protected readonly extending = computed(
    () => this.report()?.link.status === DoctorReportLinkStatus.Active);

  protected readonly ready = computed(() => {
    const issued = this.issued();
    if (issued) return issued;
    const r = this.report();
    return this.showResult() && r?.link.status === DoctorReportLinkStatus.Active ? r : null;
  });

  protected readonly title = computed(() => (this.ready() ? 'Ссылка для врача' : this.extending() ? 'Продлить ссылку' : 'Ссылка для врача'));

  protected readonly intro = computed(() => this.extending()
    ? 'Срок отсчитывается от сегодняшнего дня. Адрес ссылки не меняется.'
    : 'Врач откроет отчёт по ссылке без регистрации. По истечении срока она перестанет работать.');

  protected readonly preview = computed(() => expiryPreview(this.days()));

  constructor() {
    // Новый отчёт в диалоге — начинаем с чистого состояния.
    effect(() => {
      this.report();
      this.issued.set(null);
      this.error.set(null);
      this.days.set(DEFAULT_SHARE_DAYS);
    });
  }

  protected url(r: DoctorReport): string {
    return reportUrl(r.link.token ?? '');
  }

  protected expires(r: DoctorReport): string {
    return r.link.expiresAt ? dayMonthFromIso(r.link.expiresAt) : '—';
  }

  protected async submit(): Promise<void> {
    const r = this.report();
    if (!r || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const updated = await this.api.shareDoctorReport(r.id, this.days());
      this.issued.set(updated);
      this.changed.emit(updated);
    } catch (e) {
      this.error.set(e instanceof ApiError && e.message ? e.message : 'Не удалось выдать ссылку. Попробуйте ещё раз.');
    } finally {
      this.busy.set(false);
    }
  }

  protected async copy(r: DoctorReport): Promise<void> {
    if (await copyToClipboard(this.url(r))) this.toast.success('Ссылка скопирована в буфер обмена.');
    else this.toast.error('Не удалось скопировать — выделите ссылку вручную.');
  }

  protected async share(r: DoctorReport): Promise<void> {
    const handled = await shareLink(this.tg, this.url(r), 'Отчёт для врача', 'Мой отчёт для врача (FamilyHub)');
    if (!handled) await this.copy(r);
  }
}
