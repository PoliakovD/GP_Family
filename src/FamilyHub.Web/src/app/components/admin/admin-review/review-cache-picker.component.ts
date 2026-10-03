import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  AdminApiService,
  ReviewCacheCandidate,
  ReviewKind,
  SearchCacheSnippet,
  WebSearchTopic,
} from '../../../services/admin-api.service';
import { ApiError } from '../../../services/api.service';
import { ModalComponent } from '../../../shared/modal/modal.component';
import { ToastService } from '../../../shared/toast/toast.service';

/**
 * «Взять из готового кэша» (очередь «Одобрение», ADR-0018): поиск по всем непустым строкам кэша поиска той же темы
 * (другое написание, торговое название/МНН, похожий показатель), предпросмотр сниппетов кандидата с выбором и импорт
 * выбранных в набор задачи. После импорта набор правится в колонке «Источники», а затем принимается вместо платного
 * поиска («Использовать мой набор из кэша»). Состоянием open/closed владеет родитель (`review-detail`).
 */
@Component({
  selector: 'app-review-cache-picker',
  imports: [FormsModule, DatePipe, ModalComponent],
  template: `
    <app-modal title="Взять из готового кэша" [open]="open" [wide]="true" (closed)="close()">
      <div class="rcp">
        <p class="text-muted rcp-hint">
          Поиск по уже оплаченным результатам поиска {{ isLab ? 'показателей' : 'препаратов' }}. Выбранные источники
          добавятся в набор этой задачи — дальше их можно поправить и принять вместо платного поиска.
        </p>
        <input class="input mb-2" type="search" placeholder="Название в кэше (пусто — по словам названия задачи)"
               aria-label="Поиск по кэшу" [ngModel]="query()" (ngModelChange)="onQuery($event)" />

        @if (selected(); as cand) {
          <div class="rcp-head">
            <button type="button" class="btn btn-secondary btn-sm" (click)="back()">← К списку</button>
            <strong>{{ cand.normalizedName }}</strong>
            @if (cand.specimen) { <span class="tag tag-neutral">{{ cand.specimen }}</span> }
          </div>
          @if (detailError()) {
            <div class="alert-danger">{{ detailError() }}</div>
          } @else if (detailLoading()) {
            <p class="text-muted">Загрузка источников…</p>
          } @else {
            <div class="rcp-bulk">
              <button type="button" class="btn btn-link btn-sm" (click)="selectAll(true)">Выбрать все</button>
              <button type="button" class="btn btn-link btn-sm" (click)="selectAll(false)">Снять все</button>
            </div>
            <ul class="rcp-snippets">
              @for (s of snippets(); track s.url) {
                <li class="rcp-snippet" [class.rcp-snippet-off]="!checked().has(s.url)">
                  <label class="rcp-snippet-label">
                    <input type="checkbox" [checked]="checked().has(s.url)" (change)="toggle(s.url)" />
                    <span style="min-width: 0;">
                      @if (s.domain) {
                        <span class="tag" [class.tag-ok]="s.isTrustedByDomain" [class.tag-warning]="!s.isTrustedByDomain">{{ s.domain }}</span>
                      }
                      @if (!s.enabled) { <span class="tag tag-neutral">выключен в источнике</span> }
                      <strong class="rcp-snippet-title">{{ s.title || s.url }}</strong>
                      <span class="rcp-snippet-text">{{ s.text }}</span>
                    </span>
                  </label>
                </li>
              } @empty {
                <li class="text-muted">В этой строке кэша нет источников.</li>
              }
            </ul>
            <div class="rcp-actions">
              <button type="button" class="btn btn-primary" [disabled]="busy() || checked().size === 0" (click)="importSelected()">
                Добавить в набор ({{ checked().size }})
              </button>
              <button type="button" class="btn btn-secondary" (click)="close()">Отмена</button>
            </div>
          }
        } @else {
          @if (listError()) {
            <div class="alert-danger">{{ listError() }}</div>
          } @else if (listLoading() && candidates().length === 0) {
            <p class="text-muted">Ищем…</p>
          } @else {
            <ul class="rcp-list">
              @for (c of candidates(); track c.cacheId) {
                <li>
                  <button type="button" class="rcp-cand" (click)="pick(c)">
                    <span>
                      <strong>{{ c.normalizedName }}</strong>
                      @if (c.specimen) { <span class="tag tag-neutral">{{ c.specimen }}</span> }
                    </span>
                    <span class="text-muted">
                      {{ c.snippetCount }} источн.@if (c.manualCount > 0) { · ручных {{ c.manualCount }} }
                      · {{ c.provider }}, {{ c.lastUpdatedAt | date: 'dd.MM.yyyy' }}
                      · <span [class.text-danger]="!c.fresh">{{ c.fresh ? 'свежий' : 'устарел' }}</span>
                    </span>
                  </button>
                </li>
              } @empty {
                <li class="text-muted">Ничего не нашлось — измените запрос (например, МНН вместо торгового названия).</li>
              }
            </ul>
          }
        }
      </div>
    </app-modal>
  `,
  styles: [`
    .rcp-hint { margin-top: 0; }
    .rcp-list, .rcp-snippets { list-style: none; margin: 0; padding: 0; display: grid; gap: 6px; max-height: 55vh; overflow-y: auto; }
    .rcp-cand {
      width: 100%; text-align: left; display: flex; flex-direction: column; gap: 2px; padding: 8px 10px;
      border: 1px solid var(--color-divider); border-radius: var(--radius-md); background: var(--color-paper);
      font: inherit; color: inherit; cursor: pointer;
    }
    .rcp-cand:hover { border-color: var(--color-accent); }
    .rcp-head { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 8px; }
    .rcp-bulk { display: flex; gap: 8px; margin-bottom: 4px; }
    .rcp-snippet { padding: 6px 8px; border: 1px solid var(--color-divider); border-radius: var(--radius-md); background: var(--color-paper); }
    .rcp-snippet-off { opacity: 0.6; }
    .rcp-snippet-label { display: flex; gap: 8px; align-items: flex-start; cursor: pointer; }
    .rcp-snippet-title { display: block; margin-top: 2px; }
    .rcp-snippet-text { display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; font-size: 0.8235rem; white-space: pre-wrap; }
    .rcp-actions { display: flex; gap: 8px; margin-top: 10px; flex-wrap: wrap; }
  `],
})
export class ReviewCachePickerComponent implements OnChanges, OnDestroy {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);

  @Input() open = false;
  @Input({ required: true }) kind!: ReviewKind;
  @Input({ required: true }) id!: string;
  @Output() readonly closed = new EventEmitter<void>();
  /** Импорт прошёл — родитель перечитывает карточку и переключает выбор на «мой набор из кэша». */
  @Output() readonly imported = new EventEmitter<number>();

  readonly query = signal('');
  readonly candidates = signal<ReviewCacheCandidate[]>([]);
  readonly listLoading = signal(false);
  readonly listError = signal<string | null>(null);
  readonly selected = signal<ReviewCacheCandidate | null>(null);
  readonly snippets = signal<SearchCacheSnippet[]>([]);
  readonly checked = signal<ReadonlySet<string>>(new Set());
  readonly detailLoading = signal(false);
  readonly detailError = signal<string | null>(null);
  readonly busy = signal(false);

  private debounce: ReturnType<typeof setTimeout> | null = null;
  /** Ответ на устаревший запрос (быстрый ввод) не должен перетереть свежий список. */
  private requestSeq = 0;

  get isLab(): boolean {
    return this.kind === 'lab-analyte';
  }

  ngOnChanges(changes: SimpleChanges): void {
    if ((changes['open'] && this.open) || (this.open && (changes['id'] || changes['kind']))) {
      this.query.set('');
      this.back();
      void this.search();
    }
  }

  ngOnDestroy(): void {
    if (this.debounce) clearTimeout(this.debounce);
  }

  onQuery(value: string): void {
    this.query.set(value);
    if (this.debounce) clearTimeout(this.debounce);
    this.debounce = setTimeout(() => void this.search(), 300);
  }

  private async search(): Promise<void> {
    const seq = ++this.requestSeq;
    this.listLoading.set(true);
    this.listError.set(null);
    try {
      const rows = await this.api.getReviewCacheCandidates(this.kind, this.id, this.query());
      if (seq === this.requestSeq) this.candidates.set(rows);
    } catch {
      if (seq === this.requestSeq) this.listError.set('Не удалось загрузить кэш.');
    } finally {
      if (seq === this.requestSeq) this.listLoading.set(false);
    }
  }

  async pick(c: ReviewCacheCandidate): Promise<void> {
    this.selected.set(c);
    this.snippets.set([]);
    this.checked.set(new Set());
    this.detailError.set(null);
    this.detailLoading.set(true);
    try {
      const detail = await this.api.getSearchCacheDetail(
        c.cacheId, c.topic === 'lab-analyte' ? WebSearchTopic.LabAnalyte : WebSearchTopic.Medication);
      this.snippets.set(detail.snippets);
      // По умолчанию — то, что включено в источнике (доверенные домены/override); остальное админ отметит сам.
      this.checked.set(new Set(detail.snippets.filter((s) => s.enabled).map((s) => s.url)));
    } catch {
      this.detailError.set('Не удалось загрузить источники этой строки кэша.');
    } finally {
      this.detailLoading.set(false);
    }
  }

  back(): void {
    this.selected.set(null);
    this.snippets.set([]);
    this.checked.set(new Set());
    this.detailError.set(null);
  }

  toggle(url: string): void {
    const next = new Set(this.checked());
    if (next.has(url)) next.delete(url);
    else next.add(url);
    this.checked.set(next);
  }

  selectAll(on: boolean): void {
    this.checked.set(new Set(on ? this.snippets().map((s) => s.url) : []));
  }

  async importSelected(): Promise<void> {
    const cand = this.selected();
    if (!cand || this.busy() || this.checked().size === 0) return;
    this.busy.set(true);
    try {
      const result = await this.api.importReviewCache(this.kind, this.id, cand.cacheId, [...this.checked()]);
      this.toast.success(`Добавлено источников: ${result.imported}. Проверьте и при необходимости поправьте их.`);
      this.imported.emit(result.imported);
      this.close();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) this.toast.error('Задача уже обработана — обновите очередь.');
      else this.toast.error(e instanceof ApiError && e.detail ? e.detail : 'Не удалось добавить источники.');
    } finally {
      this.busy.set(false);
    }
  }

  close(): void {
    this.closed.emit();
  }
}
