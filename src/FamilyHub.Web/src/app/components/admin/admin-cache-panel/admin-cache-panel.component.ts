import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { AdminApiService, KbChangeTarget, KbChangeTargetValue, WebSearchTopic, WebSearchTopicValue } from '../../../services/admin-api.service';
import { KbHistoryComponent } from '../admin-review/kb-history.component';
import { ApiError } from '../../../services/api-error';
import { ToastService } from '../../../shared/toast/toast.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';

/** Одна строка редактируемой таблицы сниппетов — расширяет то, что отдаёт сервер, полем
 * originalUrl: null означает "добавлена локально, ещё не сохранена" — override для такой строки
 * недоступен (нечего переключать на сервере, пока сниппета там нет). origin/kind/note/pinned
 * не редактируются здесь, но уходят обратно при сохранении — иначе ручной/закреплённый сниппет из
 * «Одобрения» превратился бы в обычный авто. */
interface CacheSnippetRow {
  title: string;
  url: string;
  text: string;
  originalUrl: string | null;
  domain: string | null;
  isTrustedByDomain: boolean;
  override: boolean | null;
  enabled: boolean;
  origin: 'Auto' | 'Manual';
  kind: string | null;
  note: string | null;
  pinned: boolean;
}

/** Значение переключателя «Учитывать» у сниппета: auto — решает домен, on/off — ручной override. */
export type SnippetOverrideMode = 'auto' | 'on' | 'off';

/** Что поменялось в строке после сохранения — список кэша обновляет строку на месте, не сбрасывая
 * подгруженные «Показать ещё» страницы. */
export interface SearchCacheRowPatch {
  id: string;
  displayName: string | null;
  units: string | null;
  provider: string;
  snippetCount: number;
}

/**
 * Тело карточки строки кэша поиска (по образцу admin-job-panel) — полное редактирование:
 * название для людей, единицы (показатели), заголовок/ссылка/текст любого сниппета, добавление
 * нового, удаление, плюс отдельно — override конкретного URL и удаление строки целиком. Монтируется в
 * `app-side-panel` из `admin-search-cache` («Операции → Кэш поиска»).
 *
 * Ключ кэша (normalizedName) показывается, но не редактируется — по нему задачи конвейера находят
 * строку; смена отвязала бы кэш и следующий прогон заново оплатил бы поиск.
 *
 * Черновик (название, единицы, сниппеты) сохраняется кнопкой; переключатель «Учитывать» — сразу и
 * обновляет только свою строку локально, не перечитывая весь детейл, — иначе он стирал бы ещё не
 * сохранённые правки остальной таблицы.
 */
@Component({
    selector: 'app-admin-cache-panel',
    imports: [FormsModule, DatePipe, KbHistoryComponent],
    templateUrl: './admin-cache-panel.component.html',
    styleUrl: './admin-cache-panel.component.scss'
})
export class AdminCachePanelComponent implements OnChanges {
  @Input({ required: true }) cacheId!: string;
  @Input({ required: true }) topic!: WebSearchTopicValue;
  /** Эмитится после сохранения — список кэша обновляет строку (название, единицы, счётчик сниппетов). */
  @Output() readonly changed = new EventEmitter<SearchCacheRowPatch>();
  @Output() readonly deleted = new EventEmitter<void>();

  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly normalizedName = signal('');
  readonly displayName = signal('');
  readonly specimen = signal<string | null>(null);
  readonly lastUpdatedAt = signal<string | null>(null);
  readonly canBeUpdatedAfter = signal<string | null>(null);
  readonly provider = signal('');

  /** Текст поля единиц; unitsUndetermined — на сервере null («ещё не определены»). */
  readonly unitsText = signal('');
  readonly unitsUndetermined = signal(false);
  /** Админ трогал единицы (ввод или сброс) — только тогда они уходят в запрос. */
  private unitsTouched = false;
  private resetUnits = false;

  readonly rows = signal<CacheSnippetRow[]>([]);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly dirty = signal(false);

  /** История правок этой строки (журнал изменений, с откатом). */
  readonly showHistory = signal(false);

  get historyTarget(): KbChangeTargetValue {
    return this.isLabAnalyte ? KbChangeTarget.LabAnalyteSearchCache : KbChangeTarget.MedicationSearchCache;
  }

  get isLabAnalyte(): boolean {
    return this.topic === WebSearchTopic.LabAnalyte;
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['cacheId'] || changes['topic']) void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    try {
      const d = await this.api.getSearchCacheDetail(this.cacheId, this.topic);
      this.normalizedName.set(d.normalizedName);
      this.displayName.set(d.displayName ?? '');
      this.specimen.set(d.specimen);
      this.lastUpdatedAt.set(d.lastUpdatedAt);
      this.canBeUpdatedAfter.set(d.canBeUpdatedAfter);
      this.provider.set(d.provider);
      this.unitsText.set(d.units ?? '');
      this.unitsUndetermined.set(d.units === null);
      this.unitsTouched = false;
      this.resetUnits = false;
      this.rows.set(d.snippets.map((s) => ({
        title: s.title, url: s.url, text: s.text, originalUrl: s.url,
        domain: s.domain, isTrustedByDomain: s.isTrustedByDomain, override: s.override, enabled: s.enabled,
        origin: s.origin, kind: s.kind, note: s.note, pinned: s.pinned,
      })));
      this.dirty.set(false);
    } catch {
      this.toast.error('Не удалось загрузить кэш.');
    } finally {
      this.loading.set(false);
    }
  }

  markDirty(): void {
    this.dirty.set(true);
  }

  onProviderChange(value: string): void {
    this.provider.set(value);
    this.markDirty();
  }

  onDisplayNameChange(value: string): void {
    this.displayName.set(value);
    this.markDirty();
  }

  onUnitsChange(value: string): void {
    this.unitsText.set(value);
    this.unitsUndetermined.set(false);
    this.unitsTouched = true;
    this.resetUnits = false;
    this.markDirty();
  }

  /** Вернуть единицы в «не определены» — после сохранения строку снова разметит фоновая задача
   * («Пересборки → Определить единицы в кэше»). */
  resetUnitsToUndetermined(): void {
    this.unitsText.set('');
    this.unitsUndetermined.set(true);
    this.unitsTouched = true;
    this.resetUnits = true;
    this.markDirty();
  }

  addRow(): void {
    this.rows.update((rows) => [
      ...rows,
      {
        title: '', url: '', text: '', originalUrl: null, domain: null, isTrustedByDomain: false, override: null,
        enabled: false, origin: 'Auto', kind: null, note: null, pinned: false,
      },
    ]);
    this.markDirty();
  }

  removeRow(index: number): void {
    this.rows.update((rows) => rows.filter((_, i) => i !== index));
    this.markDirty();
  }

  updateRow<K extends 'title' | 'url' | 'text'>(index: number, key: K, value: string): void {
    this.rows.update((rows) => rows.map((r, i) => (i === index ? { ...r, [key]: value } : r)));
    this.markDirty();
  }

  overrideMode(row: CacheSnippetRow): SnippetOverrideMode {
    return row.override === null ? 'auto' : row.override ? 'on' : 'off';
  }

  /** «Учитывать»: по домену / да / нет. Сохраняется сразу и обновляет только эту строку локально. */
  async setOverride(row: CacheSnippetRow, mode: SnippetOverrideMode): Promise<void> {
    if (row.originalUrl === null) return;

    const next = mode === 'auto' ? null : mode === 'on';
    const enabled = next === null ? row.isTrustedByDomain : next;
    this.rows.update((rows) =>
      rows.map((r) => (r.originalUrl === row.originalUrl ? { ...r, override: next, enabled } : r)),
    );

    try {
      await this.api.setSnippetOverride(this.cacheId, this.topic, row.originalUrl, next);
    } catch {
      this.toast.error('Не удалось изменить сниппет.');
      await this.load();
    }
  }

  async save(): Promise<void> {
    for (const row of this.rows()) {
      if (!row.url.trim()) {
        this.toast.error('У каждого сниппета должна быть ссылка.');
        return;
      }
    }

    this.busy.set(true);
    try {
      await this.api.updateSearchCache(this.cacheId, {
        topic: this.topic,
        provider: this.provider().trim() || null,
        displayName: this.displayName().trim(),
        units: this.isLabAnalyte && this.unitsTouched && !this.resetUnits ? this.unitsText() : null,
        resetUnits: this.isLabAnalyte && this.resetUnits,
        snippets: this.rows().map((r) => ({
          title: r.title, url: r.url, text: r.text, origin: r.origin, kind: r.kind, note: r.note, pinned: r.pinned,
        })),
      });
      this.toast.success('Кэш сохранён.');
      await this.load();
      this.changed.emit({
        id: this.cacheId,
        displayName: this.displayName() || null,
        units: this.unitsUndetermined() ? null : this.unitsText(),
        provider: this.provider(),
        snippetCount: this.rows().length,
      });
    } catch (e) {
      this.toast.error((e instanceof ApiError && e.detail) || 'Не удалось сохранить — проверьте ссылки.');
    } finally {
      this.busy.set(false);
    }
  }

  async deleteCache(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Удалить строку кэша целиком?',
      message: `Все сниппеты по «${this.displayName() || this.normalizedName()}» будут удалены. Следующая задача обогащения оплатит поиск заново, как будто кэша никогда не было.`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;

    this.busy.set(true);
    try {
      await this.api.deleteSearchCache(this.cacheId, this.topic);
      this.toast.success('Кэш удалён.');
      this.deleted.emit();
    } catch {
      this.toast.error('Не удалось удалить кэш.');
    } finally {
      this.busy.set(false);
    }
  }
}
