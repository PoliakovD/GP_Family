import { Component, EventEmitter, Input, Output, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  AdminApiService,
  ReviewCache,
  ReviewKind,
  ReviewSource,
} from '../../../services/admin-api.service';
import { ApiError } from '../../../services/api.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { sourceKindLabel } from './review-helpers';

/** Предел длины ручной цитаты/знания — совпадает с SnippetKinds.MaxManualTextLength на бэкенде (целые статьи не принимаются). */
export const MANUAL_TEXT_LIMIT = 800;

/** Откуда брать строку кэша, если её ещё нет (стадия поиска до первого платного запроса). */
export interface EnsureCacheRef {
  kind: ReviewKind;
  id: string;
}

/**
 * Колонка «Источники» карточки очереди «Одобрение» (ADR-0018): набор сниппетов строки кэша поиска — домен и
 * доверенность, включение/выключение, закрепление, удаление и добавление своего источника (цитата со ссылкой либо
 * знание эксперта без URL). Каждое изменение сразу сохраняется (с записью в журнал) и сообщается родителю через
 * `changed`: набор изменился — черновик нужно пересуммаризовать.
 */
@Component({
  selector: 'app-review-sources',
  imports: [FormsModule, DatePipe],
  template: `
    <div class="card-title d-flex justify-content-between" style="align-items: center; gap: 8px;">
      <span>Источники</span>
      @if (!readonly) {
        <button type="button" class="btn btn-secondary btn-sm" (click)="showAdd.set(!showAdd())">
          {{ showAdd() ? 'Закрыть' : '+ Добавить источник' }}
        </button>
      }
    </div>

    @if (cache) {
      <p class="text-muted" style="margin: 0 0 8px;">
        @if (!cache.cacheId && sources.length > 0) {
          Снимок источников черновика: {{ sources.length }} · строки кэша поиска нет
        } @else {
          {{ cache.snippetCount }} в наборе @if (cache.manualCount > 0) { · ручных {{ cache.manualCount }} }
          @if (cache.provider && cache.lastUpdatedAt) {
            · {{ cache.provider }}, {{ cache.lastUpdatedAt | date: 'dd.MM.yyyy' }}
            · <span [class.text-danger]="!cache.fresh">{{ cache.fresh ? 'кэш свежий' : 'кэш устарел' }}</span>
          }
        }
        @if (cache.searchGroupKey) { · группа поиска «{{ cache.searchGroupKey }}» }
      </p>
    }

    @if (showAdd() && !readonly) {
      <form class="review-add card" (ngSubmit)="add()" style="padding: 10px; margin-bottom: 10px;">
        <div class="seg mb-2">
          <button type="button" class="seg-opt" [class.active]="addKind() === 'manual-quote'" (click)="addKind.set('manual-quote')">Цитата со ссылкой</button>
          <button type="button" class="seg-opt" [class.active]="addKind() === 'expert-knowledge'" (click)="addKind.set('expert-knowledge')">Знание эксперта</button>
        </div>
        @if (addKind() === 'manual-quote') {
          <input class="input mb-2" placeholder="Ссылка на источник (https://…)" [ngModel]="addUrl()" (ngModelChange)="addUrl.set($event)" name="url" />
        }
        <input class="input mb-2"
               [placeholder]="addKind() === 'manual-quote' ? 'Заголовок (необязательно)' : 'Заголовок (по умолчанию «Эксперт: админ»)'"
               [ngModel]="addTitle()" (ngModelChange)="addTitle.set($event)" name="title" />
        <textarea class="input mb-1" rows="4" [attr.maxlength]="textLimit"
                  [placeholder]="addKind() === 'manual-quote' ? 'Короткая цитата из источника' : 'Знание: что известно о показателе/препарате'"
                  [ngModel]="addText()" (ngModelChange)="addText.set($event)" name="text"></textarea>
        <p class="text-muted mb-2" style="margin-top: 0;">{{ addText().length }} / {{ textLimit }} — короткая выдержка, не статья целиком.</p>
        <input class="input mb-2" placeholder="Заметка (в модель не передаётся)" [ngModel]="addNote()" (ngModelChange)="addNote.set($event)" name="note" />
        <button type="submit" class="btn btn-primary btn-sm" [disabled]="busy() || !canAdd()">Добавить</button>
      </form>
    }

    <ul class="review-sources">
      @for (s of sources; track s.url) {
        <li class="review-source" [class.review-source-used]="s.usedInDraft" [class.review-source-off]="!s.enabled">
          <div class="d-flex justify-content-between" style="gap: 8px; align-items: flex-start;">
            <div style="min-width: 0;">
              <div class="review-source-head">
                @if (s.domain) {
                  <span class="tag" [class.tag-ok]="s.trustedByDomain || isExpert(s)" [class.tag-warning]="!s.trustedByDomain && !isExpert(s)">{{ s.domain }}</span>
                }
                @if (kindLabel(s); as label) { <span class="tag tag-accent">{{ label }}</span> }
                @if (s.pinned) { <span class="tag tag-accent-2">закреплён</span> }
                @if (s.usedInDraft) { <span class="tag tag-ok">использован</span> }
                @if (s.untrustedWarning) { <span class="tag tag-warning" title="Домен вне доверенных — источник включён, но проверьте его">недоверенный домен</span> }
              </div>
              @if (isExpert(s)) {
                <strong>{{ s.title }}</strong>
              } @else {
                <a [href]="s.url" target="_blank" rel="noopener noreferrer"><strong>{{ s.title || s.url }}</strong></a>
              }
            </div>
            @if (!readonly) {
              <label class="switch" [title]="s.enabled ? 'Включён в набор' : 'Выключен'">
                <input type="checkbox" [checked]="s.enabled" [disabled]="busy()" [attr.aria-label]="'Включить источник ' + (s.title || s.url)"
                       (change)="toggle(s, $event)" />
                <span class="switch-knob"></span>
              </label>
            }
          </div>
          <p class="review-source-text" [class.review-source-text-open]="expanded() === s.url" (click)="expand(s.url)">{{ s.text }}</p>
          @if (s.note) { <p class="text-muted mb-1">Заметка: {{ s.note }}</p> }
          @if (!readonly) {
            <div class="d-flex gap-2" style="flex-wrap: wrap;">
              <button type="button" class="btn btn-secondary btn-sm" [disabled]="busy()" (click)="pin(s)">{{ s.pinned ? 'Открепить' : 'Закрепить' }}</button>
              @if (s.override !== null) {
                <button type="button" class="btn btn-secondary btn-sm" [disabled]="busy()" (click)="resetOverride(s)">По умолчанию</button>
              }
              <button type="button" class="btn btn-secondary btn-sm" [disabled]="busy()" (click)="remove(s)">Удалить</button>
            </div>
          }
        </li>
      } @empty {
        <li class="text-muted">Источников нет{{ readonly ? '.' : ' — добавьте свой или запустите платный поиск.' }}</li>
      }
    </ul>
  `,
  styles: [`
    .review-sources { list-style: none; margin: 0; padding: 0; display: grid; gap: 10px; }
    .review-source { padding: 8px 10px; border: 1px solid var(--color-divider); border-radius: var(--radius-md); background: var(--color-paper); }
    .review-source-used { border-color: var(--color-accent); }
    .review-source-off { opacity: 0.6; }
    .review-source-head { display: flex; gap: 4px; flex-wrap: wrap; margin-bottom: 4px; }
    .review-source-text { margin: 6px 0; font-size: 0.8235rem; cursor: pointer; display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; white-space: pre-wrap; }
    .review-source-text-open { display: block; -webkit-line-clamp: unset; }
  `],
})
export class ReviewSourcesComponent {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  @Input() sources: readonly ReviewSource[] = [];
  @Input() cache: ReviewCache | null = null;
  /** Только просмотр (снимок черновика без строки кэша). */
  @Input() readonly = false;
  /** Для стадии поиска: если строки кэша ещё нет, создаём её перед первым изменением. */
  @Input() ensureRef: EnsureCacheRef | null = null;
  /** Набор изменился (источник добавлен/удалён/включён/закреплён) — родитель перечитывает карточку. */
  @Output() readonly changed = new EventEmitter<void>();

  readonly textLimit = MANUAL_TEXT_LIMIT;
  readonly showAdd = signal(false);
  readonly busy = signal(false);
  readonly expanded = signal<string | null>(null);
  readonly addKind = signal<'manual-quote' | 'expert-knowledge'>('manual-quote');
  readonly addUrl = signal('');
  readonly addTitle = signal('');
  readonly addText = signal('');
  readonly addNote = signal('');

  kindLabel(s: ReviewSource): string | null {
    return sourceKindLabel(s.kind, s.origin);
  }

  isExpert(s: ReviewSource): boolean {
    return s.kind === 'expert-knowledge';
  }

  expand(url: string): void {
    this.expanded.set(this.expanded() === url ? null : url);
  }

  canAdd(): boolean {
    const text = this.addText().trim();
    if (text.length === 0 || text.length > MANUAL_TEXT_LIMIT) return false;
    return this.addKind() === 'expert-knowledge' || this.addUrl().trim().length > 0;
  }

  /** Возвращает id строки кэша, при необходимости создавая её (стадия поиска до первого платного запроса). */
  private async cacheId(): Promise<string | null> {
    if (this.cache?.cacheId) return this.cache.cacheId;
    if (!this.ensureRef) return null;
    return (await this.api.ensureReviewCache(this.ensureRef.kind, this.ensureRef.id)).cacheId;
  }

  private get topic(): 'lab-analyte' | 'medication' {
    return this.cache?.topic ?? 'medication';
  }

  private async run(action: (cacheId: string) => Promise<unknown>, okText?: string): Promise<boolean> {
    this.busy.set(true);
    try {
      const id = await this.cacheId();
      if (!id) {
        this.toast.error('Строка кэша не найдена.');
        return false;
      }
      await action(id);
      if (okText) this.toast.success(okText);
      this.changed.emit();
      return true;
    } catch (e) {
      this.toast.error(e instanceof ApiError && e.detail ? e.detail : 'Не удалось изменить набор источников.');
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  async add(): Promise<void> {
    if (!this.canAdd()) return;
    const ok = await this.run(
      (id) => this.api.addManualSnippet(this.topic, id, {
        kind: this.addKind(), url: this.addUrl().trim() || null, title: this.addTitle().trim() || null,
        text: this.addText().trim(), note: this.addNote().trim() || null,
      }),
      'Источник добавлен — пересуммируйте, чтобы он попал в черновик.',
    );
    if (ok) {
      this.addUrl.set('');
      this.addTitle.set('');
      this.addText.set('');
      this.addNote.set('');
      this.showAdd.set(false);
    }
  }

  async toggle(s: ReviewSource, event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const ok = await this.run((id) => this.api.setSnippetEnabled(this.topic, id, s.url, !s.enabled));
    if (!ok) input.checked = s.enabled;
  }

  async resetOverride(s: ReviewSource): Promise<void> {
    await this.run((id) => this.api.setSnippetEnabled(this.topic, id, s.url, null));
  }

  async pin(s: ReviewSource): Promise<void> {
    await this.run((id) => this.api.setSnippetPinned(this.topic, id, s.url, !s.pinned));
  }

  async remove(s: ReviewSource): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Удалить источник из набора?',
      message: `«${s.title || s.url}» будет удалён из набора (изменение попадёт в историю и его можно откатить).`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;
    await this.run((id) => this.api.removeSnippet(this.topic, id, s.url));
  }
}
