import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { AdminApiService, WebSearchTopicValue } from '../../../services/admin-api.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';

/** Одна строка редактируемой таблицы сниппетов — расширяет то, что отдаёт сервер, полем
 * originalUrl: null означает "добавлена локально, ещё не сохранена" — override для такой строки
 * недоступен (нечего переключать на сервере, пока сниппета там нет). */
interface CacheSnippetRow {
  title: string;
  url: string;
  text: string;
  originalUrl: string | null;
  domain: string | null;
  isTrustedByDomain: boolean;
  override: boolean | null;
  enabled: boolean;
}

/**
 * Тело карточки строки кэша поиска (по образцу admin-job-panel) — полное редактирование:
 * заголовок/ссылка/текст любого сниппета, добавление нового, удаление, плюс отдельно —
 * доверие/override конкретного URL (как раньше) и удаление строки целиком. Монтируется в
 * `app-side-panel` из `admin-enrichment` (вкладка «Кэш поиска»).
 *
 * Черновик таблицы сниппетов и тумблер override — НЕЗАВИСИМЫЕ действия: override шлётся на
 * сервер сразу (тот же тройной клик, что был раньше) и обновляет только свою строку локально, не
 * перечитывая весь детейл — иначе клик по чекбоксу стирал бы ещё не сохранённые правки
 * заголовков/добавленные строки в остальной таблице.
 */
@Component({
  selector: 'app-admin-cache-panel',
  standalone: true,
  imports: [FormsModule, DatePipe],
  templateUrl: './admin-cache-panel.component.html',
})
export class AdminCachePanelComponent implements OnChanges {
  @Input({ required: true }) cacheId!: string;
  @Input({ required: true }) topic!: WebSearchTopicValue;
  /** Эмитится после сохранения/удаления override — родитель (список кэша) может захотеть
   * обновить счётчик сниппетов в таблице. */
  @Output() readonly changed = new EventEmitter<void>();
  @Output() readonly deleted = new EventEmitter<void>();

  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly normalizedName = signal('');
  readonly specimen = signal<string | null>(null);
  readonly lastUpdatedAt = signal<string | null>(null);
  readonly canBeUpdatedAfter = signal<string | null>(null);
  readonly provider = signal('');

  readonly rows = signal<CacheSnippetRow[]>([]);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly dirty = signal(false);

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['cacheId'] || changes['topic']) void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    try {
      const d = await this.api.getSearchCacheDetail(this.cacheId, this.topic);
      this.normalizedName.set(d.normalizedName);
      this.specimen.set(d.specimen);
      this.lastUpdatedAt.set(d.lastUpdatedAt);
      this.canBeUpdatedAfter.set(d.canBeUpdatedAfter);
      this.provider.set(d.provider);
      this.rows.set(d.snippets.map((s) => ({
        title: s.title, url: s.url, text: s.text, originalUrl: s.url,
        domain: s.domain, isTrustedByDomain: s.isTrustedByDomain, override: s.override, enabled: s.enabled,
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

  addRow(): void {
    this.rows.update((rows) => [
      ...rows,
      { title: '', url: '', text: '', originalUrl: null, domain: null, isTrustedByDomain: false, override: null, enabled: false },
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

  /** Тройной клик, как раньше в admin-enrichment: не задано → включено → выключено → не задано.
   * Обновляет только эту строку локально — не перечитывает весь список, чтобы не стереть ещё не
   * сохранённые правки остальных строк. */
  async cycleOverride(row: CacheSnippetRow): Promise<void> {
    if (row.originalUrl === null) return;

    const next = row.override === null ? true : row.override === true ? false : null;
    const enabled = next === null ? row.isTrustedByDomain : next;
    this.rows.update((rows) =>
      rows.map((r) => (r.originalUrl === row.originalUrl ? { ...r, override: next, enabled } : r)),
    );

    try {
      await this.api.setSnippetOverride(this.cacheId, this.topic, row.originalUrl, next);
      this.changed.emit();
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
        snippets: this.rows().map((r) => ({ title: r.title, url: r.url, text: r.text })),
      });
      this.toast.success('Кэш сохранён.');
      await this.load();
      this.changed.emit();
    } catch {
      this.toast.error('Не удалось сохранить — проверьте ссылки.');
    } finally {
      this.busy.set(false);
    }
  }

  async deleteCache(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Удалить строку кэша целиком?',
      message: `Все сниппеты по «${this.normalizedName()}» будут удалены. Следующая задача обогащения оплатит поиск заново, как будто кэша никогда не было.`,
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
