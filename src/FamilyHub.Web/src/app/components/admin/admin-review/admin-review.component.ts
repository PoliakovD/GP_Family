import { Component, HostListener, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  AdminApiService,
  BulkApproveItem,
  ReviewInboxItem,
  ReviewKind,
  ReviewStage,
} from '../../../services/admin-api.service';
import { ApiError } from '../../../services/api.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { ReviewQueueStore } from '../shared/review-queue.store';
import { ReviewConfigComponent } from './review-config.component';
import { ReviewActed, ReviewDetailComponent } from './review-detail.component';
import {
  confidenceLabel,
  indexAfterRemoval,
  isTypingTarget,
  reviewKindLabel,
  reviewOriginLabel,
  reviewStageLabel,
} from './review-helpers';

type StageFilter = 'all' | ReviewStage;

/** Ключ строки Inbox — вид + стадия + id (одна и та же задача не бывает на двух стадиях одновременно, но ключ явный). */
export function inboxKey(item: { kind: ReviewKind; stage: ReviewStage; id: string }): string {
  return `${item.kind}:${item.stage}:${item.id}`;
}

/**
 * «Одобрение» (ADR-0018) — единый Inbox: очередь слева (платные поиски и результаты с низкой уверенностью вперемешку,
 * «ниже порога» красным и первыми), карточка справа. Работа с клавиатуры: J/K или стрелки — навигация, A — одобрить,
 * R — отклонить, E — править, после действия автопереход к следующей задаче. Клик по названию открывает карточку записи
 * справочника вне очереди. Пакетные действия — для платных поисков (отметить чекбоксами). Внизу — «Настройки confidence».
 */
@Component({
  selector: 'app-admin-review',
  imports: [FormsModule, DatePipe, ReviewDetailComponent, ReviewConfigComponent],
  templateUrl: './admin-review.component.html',
  styleUrl: './admin-review.component.scss',
})
export class AdminReviewComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly queue = inject(ReviewQueueStore);

  @ViewChild(ReviewDetailComponent) private detail?: ReviewDetailComponent;

  readonly rows = signal<ReviewInboxItem[]>([]);
  readonly searchesTotal = signal(0);
  readonly resultsTotal = signal(0);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly kindFilter = signal<ReviewKind | ''>('');
  readonly stageFilter = signal<StageFilter>('all');
  readonly selectedKey = signal<string | null>(null);
  readonly checked = signal<ReadonlySet<string>>(new Set());
  readonly bulkBusy = signal(false);
  readonly showConfig = signal(false);
  /** Карточка записи справочника вне очереди (вариант C) — поверх выбранной задачи. */
  readonly entityRef = signal<{ kind: ReviewKind; kbId: string } | null>(null);

  readonly confidenceLabel = confidenceLabel;
  readonly kindLabel = reviewKindLabel;
  readonly originLabel = reviewOriginLabel;
  readonly stageLabel = reviewStageLabel;
  readonly inboxKey = inboxKey;

  readonly selectedIndex = computed(() => this.rows().findIndex((r) => inboxKey(r) === this.selectedKey()));
  readonly selected = computed(() => this.rows()[this.selectedIndex()] ?? null);

  /** Отмеченные платные поиски — единственные, к которым применимо пакетное действие. */
  readonly checkedSearches = computed(() => this.rows().filter((r) => r.stage === 'search' && this.checked().has(inboxKey(r))));
  readonly allSearchesChecked = computed(() => {
    const searches = this.rows().filter((r) => r.stage === 'search');
    return searches.length > 0 && searches.every((r) => this.checked().has(inboxKey(r)));
  });
  readonly belowCount = computed(() => this.rows().filter((r) => r.belowThreshold).length);

  ngOnInit(): void {
    void this.load();
  }

  async load(selectKey: string | null = null): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const kind = this.kindFilter() || null;
      const stage = this.stageFilter() === 'all' ? null : this.stageFilter() as ReviewStage;
      const inbox = await this.api.getReviewInbox(kind, stage);
      this.rows.set(inbox.rows);
      this.searchesTotal.set(inbox.searches);
      this.resultsTotal.set(inbox.results);
      this.checked.set(new Set([...this.checked()].filter((k) => inbox.rows.some((r) => inboxKey(r) === k))));

      const wanted = selectKey ?? this.selectedKey();
      const keep = inbox.rows.find((r) => inboxKey(r) === wanted);
      this.selectedKey.set(keep ? inboxKey(keep) : inbox.rows[0] ? inboxKey(inbox.rows[0]) : null);
    } catch {
      this.error.set('Не удалось загрузить очередь.');
    } finally {
      this.loading.set(false);
      void this.queue.refresh();
    }
  }

  setKind(kind: ReviewKind | ''): void {
    this.kindFilter.set(kind);
    void this.load();
  }

  setStage(stage: StageFilter): void {
    this.stageFilter.set(stage);
    void this.load();
  }

  select(item: ReviewInboxItem): void {
    this.entityRef.set(null);
    this.selectedKey.set(inboxKey(item));
  }

  private move(delta: number): void {
    const rows = this.rows();
    if (rows.length === 0) return;
    const index = this.selectedIndex();
    const next = Math.min(Math.max((index < 0 ? 0 : index + delta), 0), rows.length - 1);
    this.entityRef.set(null);
    this.selectedKey.set(inboxKey(rows[next]));
    queueMicrotask(() => document.querySelector('.inbox-row-active')?.scrollIntoView({ block: 'nearest' }));
  }

  // ------------------------------------------------------------------ горячие клавиши

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    if (event.ctrlKey || event.metaKey || event.altKey) return;
    if (isTypingTarget(event.target) || this.confirm.request()) return;

    switch (event.key) {
      case 'j':
      case 'J':
      case 'ArrowDown':
        event.preventDefault();
        this.move(1);
        break;
      case 'k':
      case 'K':
      case 'ArrowUp':
        event.preventDefault();
        this.move(-1);
        break;
      case 'a':
      case 'A':
        event.preventDefault();
        void this.detail?.approve();
        break;
      case 'r':
      case 'R':
        event.preventDefault();
        void this.detail?.reject();
        break;
      case 'e':
      case 'E':
        event.preventDefault();
        this.detail?.edit();
        break;
      default:
        break;
    }
  }

  // ------------------------------------------------------------------ результат действия в карточке

  /** Действие сделано — задача уходит из очереди, открывается следующая (автопереход). */
  onActed(ev: ReviewActed): void {
    void this.queue.refresh();
    if (ev.type === 'verified') return;

    const rows = this.rows();
    const index = rows.findIndex((r) => r.kind === ev.kind && r.id === ev.id);
    if (index < 0) return;

    const remaining = rows.filter((_, i) => i !== index);
    this.rows.set(remaining);
    if (ev.stage === 'search') this.searchesTotal.update((n) => Math.max(n - 1, 0));
    if (ev.stage === 'result') this.resultsTotal.update((n) => Math.max(n - 1, 0));

    const nextIndex = indexAfterRemoval(index, rows.length);
    this.selectedKey.set(nextIndex < 0 ? null : inboxKey(remaining[nextIndex]));
    this.checked.update((s) => new Set([...s].filter((k) => k !== inboxKey(rows[index]))));
    this.entityRef.set(null);
  }

  onSkip(): void {
    this.move(1);
  }

  openEntity(ref: { kind: ReviewKind; kbId: string }): void {
    this.entityRef.set(ref);
  }

  /** Из карточки записи — перейти к её задаче в очереди (если она в текущей выборке). */
  openQueueItem(ref: { kind: ReviewKind; id: string }): void {
    const row = this.rows().find((r) => r.kind === ref.kind && r.id === ref.id);
    if (row) {
      this.select(row);
    } else {
      this.entityRef.set(null);
      void this.load(null);
    }
  }

  closeEntity(): void {
    this.entityRef.set(null);
  }

  // ------------------------------------------------------------------ пакетные действия

  toggleChecked(item: ReviewInboxItem, event: Event): void {
    event.stopPropagation();
    const key = inboxKey(item);
    this.checked.update((s) => {
      const next = new Set(s);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  toggleAllSearches(): void {
    const searches = this.rows().filter((r) => r.stage === 'search');
    if (this.allSearchesChecked()) this.checked.set(new Set());
    else this.checked.set(new Set(searches.map((r) => inboxKey(r))));
  }

  async bulkApprove(): Promise<void> {
    const items = this.checkedSearches();
    if (items.length === 0 || this.bulkBusy()) return;

    const ok = await this.confirm.confirm({
      title: `Одобрить ${items.length} платных поисков?`,
      message: 'Каждый поиск уйдёт во внешний платный поиск (а если у показателя есть бесплатный кэш двойника — он НЕ будет подставлен автоматически: пакетно одобряется именно платный поиск).',
      confirmText: 'Одобрить',
    });
    if (!ok) return;

    this.bulkBusy.set(true);
    try {
      const payload: BulkApproveItem[] = items.map((i) => ({ kind: i.kind, id: i.id, queryText: null }));
      const res = await this.api.bulkApproveReviewSearches(payload);
      this.toast.success(`Одобрено: ${res.processedCount}${res.failedItems.length ? `, не удалось: ${res.failedItems.length}` : ''}.`);
      this.checked.set(new Set());
      await this.load();
    } catch (e) {
      this.toast.error(e instanceof ApiError && e.detail ? e.detail : 'Не удалось одобрить пачку.');
    } finally {
      this.bulkBusy.set(false);
    }
  }

  async bulkReject(): Promise<void> {
    const items = this.checkedSearches();
    if (items.length === 0 || this.bulkBusy()) return;

    const ok = await this.confirm.confirm({
      title: `Отклонить ${items.length} платных поисков?`,
      message: 'Задачи завершатся как «отклонено администратором», внешний поиск по ним выполнен не будет.',
      confirmText: 'Отклонить',
      danger: true,
    });
    if (!ok) return;

    this.bulkBusy.set(true);
    try {
      const res = await this.api.bulkRejectReviewSearches(items.map((i) => ({ kind: i.kind, id: i.id })));
      this.toast.info(`Отклонено: ${res.processedCount}${res.failedItems.length ? `, не удалось: ${res.failedItems.length}` : ''}.`);
      this.checked.set(new Set());
      await this.load();
    } catch (e) {
      this.toast.error(e instanceof ApiError && e.detail ? e.detail : 'Не удалось отклонить пачку.');
    } finally {
      this.bulkBusy.set(false);
    }
  }
}
