import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import {
  AdminApiService,
  KbChangeLogDetail,
  KbChangeLogItem,
  KbChangeTargetValue,
} from '../../../services/admin-api.service';
import { ApiError } from '../../../services/api.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { HistoryDiffLine, diffSnapshots, historyActionLabel, historyTargetLabel } from './review-helpers';

/**
 * История правок записи справочника или её кэша поиска (ADR-0018): кто/когда/что изменилось («было → стало») и
 * откат одной версии. «Кто» различает правку админа и запись автообогащения (админ платформы — единая учётка без
 * User, личность не хранится). Используется в карточке Inbox/сущности и в каталоге админки.
 */
@Component({
  selector: 'app-kb-history',
  imports: [DatePipe],
  template: `
    <div class="card-title d-flex justify-content-between" style="align-items: center;">
      <span>{{ title }}</span>
      <button type="button" class="btn btn-secondary btn-sm" [disabled]="loading()" (click)="load()">Обновить</button>
    </div>
    @if (loading() && items().length === 0) {
      <p class="text-muted">Загрузка…</p>
    } @else if (error()) {
      <div class="alert-danger">{{ error() }}</div>
    } @else {
      <ul class="kb-history">
        @for (h of items(); track h.id) {
          <li class="kb-history-item">
            <div class="d-flex justify-content-between" style="align-items: baseline; gap: 8px; flex-wrap: wrap;">
              <div>
                <span class="tag" [class.tag-accent]="h.actor === 'admin'" [class.tag-neutral]="h.actor !== 'admin'">
                  {{ h.actor === 'admin' ? 'админ' : 'система' }}
                </span>
                <strong>{{ actionLabel(h.action) }}</strong>
                @if (showRecord) { «{{ h.targetLabel }}» }
                <span class="text-muted"> · {{ targetLabel(h.target) }} · {{ h.at | date: 'dd.MM.yyyy HH:mm' }}</span>
                @if (h.revertedLogId) { <span class="tag tag-neutral">откат</span> }
              </div>
              <div class="d-flex gap-2">
                <button type="button" class="btn btn-secondary btn-sm" (click)="toggle(h)">
                  {{ expandedId() === h.id ? 'Скрыть' : 'Что изменилось' }}
                </button>
                @if (h.canRevert) {
                  <button type="button" class="btn btn-secondary btn-sm" [disabled]="busyId() === h.id" (click)="revert(h)">
                    Откатить
                  </button>
                }
              </div>
            </div>
            @if (h.note) { <p class="text-muted mb-0" style="margin-top: 2px;">{{ h.note }}</p> }
            @if (expandedId() === h.id) {
              @if (diffLoading()) {
                <p class="text-muted">Загрузка…</p>
              } @else if (diff().length === 0) {
                <p class="text-muted mb-0">Различий в сохранённых полях нет.</p>
              } @else {
                <table class="table">
                  <thead><tr><th>Поле</th><th>Было</th><th>Стало</th></tr></thead>
                  <tbody>
                    @for (line of diff(); track $index) {
                      <tr>
                        <td>{{ line.label }}</td>
                        <td class="kb-history-before">{{ line.before }}</td>
                        <td class="kb-history-after">{{ line.after }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            }
          </li>
        } @empty {
          <li class="text-muted">Изменений пока не было.</li>
        }
      </ul>
      @if (items().length < total()) {
        <button type="button" class="btn btn-secondary btn-sm" [disabled]="loading()" (click)="loadMore()">Показать ещё</button>
      }
    }
  `,
  styles: [`
    .kb-history { list-style: none; margin: 0; padding: 0; display: grid; gap: 10px; }
    .kb-history-item { padding-bottom: 10px; border-bottom: 1px solid var(--color-divider); }
    .kb-history-before { color: var(--color-status-danger-text); white-space: pre-wrap; word-break: break-word; }
    .kb-history-after { color: var(--color-status-ok-text); white-space: pre-wrap; word-break: break-word; }
  `],
})
export class KbHistoryComponent implements OnChanges {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  @Input() title = 'История правок';
  /** Фильтр по типу записи (null — все). */
  @Input() target: KbChangeTargetValue | null = null;
  /** Id строки справочника/кэша (null — вся история по типу). */
  @Input() targetId: string | null = null;
  /** Показывать название записи в каждой строке — для общего журнала, где записи разные. */
  @Input() showRecord = false;
  /** Родитель перечитывает свою запись после успешного отката. */
  @Output() readonly reverted = new EventEmitter<void>();

  private readonly pageSize = 20;
  readonly items = signal<KbChangeLogItem[]>([]);
  readonly total = signal(0);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly expandedId = signal<string | null>(null);
  readonly diff = signal<HistoryDiffLine[]>([]);
  readonly diffLoading = signal(false);
  readonly busyId = signal<string | null>(null);

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['target'] || changes['targetId']) void this.load();
  }

  actionLabel = historyActionLabel;
  targetLabel = historyTargetLabel;

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await this.api.getHistory(this.target, this.targetId, 0, this.pageSize);
      this.items.set(page.items);
      this.total.set(page.total);
    } catch {
      this.error.set('Не удалось загрузить историю.');
    } finally {
      this.loading.set(false);
    }
  }

  async loadMore(): Promise<void> {
    this.loading.set(true);
    try {
      const page = await this.api.getHistory(this.target, this.targetId, this.items().length, this.pageSize);
      this.items.update((list) => [...list, ...page.items]);
      this.total.set(page.total);
    } catch {
      this.toast.error('Не удалось загрузить историю.');
    } finally {
      this.loading.set(false);
    }
  }

  async toggle(h: KbChangeLogItem): Promise<void> {
    if (this.expandedId() === h.id) {
      this.expandedId.set(null);
      return;
    }
    this.expandedId.set(h.id);
    this.diff.set([]);
    this.diffLoading.set(true);
    try {
      const detail: KbChangeLogDetail = await this.api.getHistoryDetail(h.id);
      this.diff.set(diffSnapshots(detail.beforeJson, detail.afterJson));
    } catch {
      this.toast.error('Не удалось загрузить детали изменения.');
    } finally {
      this.diffLoading.set(false);
    }
  }

  async revert(h: KbChangeLogItem): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Откатить это изменение?',
      message: `«${h.targetLabel}» вернётся к состоянию до записи «${historyActionLabel(h.action)}» от ${new Date(h.at).toLocaleString('ru-RU')}. Сам откат тоже попадёт в историю.`,
      confirmText: 'Откатить',
      danger: true,
    });
    if (!ok) return;

    this.busyId.set(h.id);
    try {
      await this.api.revertHistory(h.id);
      this.toast.success('Изменение откачено.');
      await this.load();
      this.reverted.emit();
    } catch (e) {
      this.toast.error(e instanceof ApiError && e.detail ? e.detail : 'Не удалось откатить изменение.');
    } finally {
      this.busyId.set(null);
    }
  }
}
