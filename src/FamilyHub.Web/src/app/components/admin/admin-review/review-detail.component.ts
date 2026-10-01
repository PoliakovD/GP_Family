import {
  Component,
  ElementRef,
  EventEmitter,
  Input,
  OnChanges,
  Output,
  SimpleChanges,
  ViewChild,
  computed,
  inject,
  signal,
} from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  AdminApiService,
  KbChangeTarget,
  KbChangeTargetValue,
  ReviewEntity,
  ReviewItemDetail,
  ReviewKind,
  ReviewResummarizePreview,
  ReviewStage,
} from '../../../services/admin-api.service';
import { ApiError } from '../../../services/api.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { AdminPayloadEditorComponent, PayloadSaveEvent } from '../admin-payload-editor/admin-payload-editor.component';
import { VerificationBadgeComponent } from '../shared/verification-badge.component';
import { KbHistoryComponent } from './kb-history.component';
import { ReviewSourcesComponent } from './review-sources.component';
import {
  PayloadFieldDiff,
  confidenceLabel,
  diffPayload,
  payloadFieldLabel,
  previewValue,
  reviewKindLabel,
  reviewOriginLabel,
  reviewStageLabel,
} from './review-helpers';

/** Что произошло в карточке — родитель (Inbox) убирает задачу из очереди и открывает следующую. */
export interface ReviewActed {
  type: 'approved' | 'rejected' | 'verified';
  kind: ReviewKind;
  id: string;
  stage: ReviewStage | null;
}

/** Выбор режима одобрения поиска: платный поиск, свой набор из кэша либо кэш «двойника» (другая группа биоматериалов). */
type SearchChoice = { type: 'paid' } | { type: 'own' } | { type: 'twin'; cacheId: string };

/**
 * Карточка очереди «Одобрение» (ADR-0018) — три колонки: источники (сниппеты кэша: домен, доверенность, включение,
 * закрепление, добавление своего), черновик/запись справочника (отличия от текущей записи, откуда взято каждое поле,
 * поля без источника — жёлтым, редактор payload) и вердикт (уверенность и причина, действия, заметка админа).
 * Два режима: «item» — задача очереди (стадия поиска либо результата), «entity» — запись справочника вне очереди
 * (вариант C: открывается по клику на имя, править, пересуммировать, отметить проверенной, история).
 */
@Component({
  selector: 'app-review-detail',
  imports: [FormsModule, DatePipe, AdminPayloadEditorComponent, ReviewSourcesComponent, KbHistoryComponent, VerificationBadgeComponent],
  templateUrl: './review-detail.component.html',
  styleUrl: './review-detail.component.scss',
})
export class ReviewDetailComponent implements OnChanges {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  @Input({ required: true }) kind!: ReviewKind;
  /** Режим «item»: id задачи очереди; режим «entity»: id записи справочника (kb). */
  @Input({ required: true }) id!: string;
  @Input() mode: 'item' | 'entity' = 'item';

  @Output() readonly acted = new EventEmitter<ReviewActed>();
  /** Клик по названию — открыть карточку записи справочника вне очереди. */
  @Output() readonly openEntity = new EventEmitter<{ kind: ReviewKind; kbId: string }>();
  /** Из карточки записи — перейти к её задаче в очереди. */
  @Output() readonly openQueueItem = new EventEmitter<{ kind: ReviewKind; id: string }>();
  @Output() readonly skipped = new EventEmitter<void>();

  @ViewChild(AdminPayloadEditorComponent) private editor?: AdminPayloadEditorComponent;
  @ViewChild('editorHost') private editorHost?: ElementRef<HTMLElement>;

  readonly item = signal<ReviewItemDetail | null>(null);
  readonly entity = signal<ReviewEntity | null>(null);
  readonly preview = signal<ReviewResummarizePreview | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  // Редактируемые поля
  readonly queryText = signal('');
  readonly displayName = signal('');
  readonly aliasesText = signal('');
  readonly note = signal('');
  readonly choice = signal<SearchChoice>({ type: 'paid' });
  /** Набор источников менялся после последней суммаризации — подсказка «Пересуммировать». */
  readonly sourcesDirty = signal(false);
  /** Редактор payload перечитывает значение при смене этой строки (применение предложения пересуммаризации). */
  readonly editorPayload = signal('{}');
  readonly showHistory = signal(false);

  readonly confidenceLabel = confidenceLabel;
  readonly kindLabel = reviewKindLabel;
  readonly originLabel = reviewOriginLabel;
  readonly stageLabel = reviewStageLabel;

  readonly isSearch = computed(() => this.mode === 'item' && this.item()?.stage === 'search');
  readonly isResult = computed(() => this.mode === 'item' && this.item()?.stage === 'result');

  /** Отличия «предлагаемого» payload от текущей записи справочника: черновик задачи либо предложение пересуммаризации. */
  readonly diffs = computed<PayloadFieldDiff[]>(() => {
    const item = this.item();
    if (item?.draft) return diffPayload(item.draft.payloadJson, item.current?.payloadJson ?? null);
    const entity = this.entity();
    const preview = this.preview();
    if (entity && preview) return diffPayload(preview.payloadJson, entity.current.payloadJson);
    return [];
  });

  readonly diffKeys = computed(() => this.diffs().map((d) => d.key));

  /** Поля без источника (модель не указала сниппет) — жёлтым; пусто, если модель не вернула атрибуцию вообще. */
  readonly warnKeys = computed(() => (this.item()?.fieldSourceInfo ?? this.preview()?.fieldSourceInfo)?.fieldsWithoutSource ?? []);

  /** «Откуда поле»: поле → домены источников (из атрибуции модели). */
  readonly fieldSourceRows = computed(() => {
    const info = this.item()?.fieldSourceInfo ?? this.preview()?.fieldSourceInfo;
    if (!info?.available) return [];
    return Object.entries(info.fieldSources).map(([key, urls]) => ({
      key,
      label: payloadFieldLabel(key),
      domains: [...new Set(urls.map((u) => this.domainOf(u)))],
    }));
  });

  readonly kbTarget = computed<KbChangeTargetValue>(() =>
    this.kind === 'lab-analyte' ? KbChangeTarget.LabAnalyteKb : KbChangeTarget.MedicationKb);

  readonly kbId = computed(() => this.entity()?.current.id ?? this.item()?.current?.id ?? null);

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['id'] || changes['kind'] || changes['mode']) void this.load();
  }

  async load(keepNote = false): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      if (this.mode === 'entity') {
        const entity = await this.api.getReviewEntity(this.kind, this.id);
        this.entity.set(entity);
        this.item.set(null);
        this.preview.set(null);
        this.editorPayload.set(entity.current.payloadJson);
        this.displayName.set(entity.current.displayName);
        this.aliasesText.set(entity.current.aliases.join(', '));
      } else {
        const item = await this.api.getReviewItem(this.kind, this.id);
        this.item.set(item);
        this.entity.set(null);
        this.preview.set(null);
        this.queryText.set(item.queryText);
        if (!keepNote) this.note.set(item.note ?? '');
        this.displayName.set(item.draft?.displayName ?? '');
        this.aliasesText.set((item.draft?.aliases ?? []).join(', '));
        this.editorPayload.set(item.draft?.payloadJson ?? '{}');
        this.choice.set(this.defaultChoice(item));
        this.sourcesDirty.set(false);
      }
    } catch (e) {
      this.error.set(e instanceof ApiError && e.status === 404
        ? 'Задача уже обработана или удалена.'
        : 'Не удалось загрузить карточку.');
    } finally {
      this.loading.set(false);
    }
  }

  /** По умолчанию — бесплатное: кэш «двойника» (другой биоматериал той же группы показателей), затем свой набор; платный
   * поиск — явный выбор «искать отдельно». */
  private defaultChoice(item: ReviewItemDetail): SearchChoice {
    if (item.stage !== 'search') return { type: 'paid' };
    if (item.twins.length > 0) return { type: 'twin', cacheId: item.twins[0].cacheId };
    if (item.cache.snippetCount > 0 && item.sources.some((s) => s.enabled)) return { type: 'own' };
    return { type: 'paid' };
  }

  isChoice(type: SearchChoice['type'], cacheId?: string): boolean {
    const c = this.choice();
    return c.type === type && (type !== 'twin' || (c as { cacheId: string }).cacheId === cacheId);
  }

  pickChoice(type: SearchChoice['type'], cacheId?: string): void {
    this.choice.set(type === 'twin' ? { type: 'twin', cacheId: cacheId! } : { type });
  }

  private domainOf(url: string): string {
    if (url.startsWith('expert://')) return 'Эксперт: админ';
    try {
      return new URL(url).host;
    } catch {
      return url;
    }
  }

  previewText(value: unknown): string {
    return previewValue(value);
  }

  fieldLabel(key: string): string {
    return payloadFieldLabel(key);
  }

  /** Кнопка в шапке: открыть карточку записи справочника по клику на название. */
  openEntityCard(): void {
    const kbId = this.kbId();
    if (kbId) this.openEntity.emit({ kind: this.kind, kbId });
  }

  onSourcesChanged(): void {
    this.sourcesDirty.set(true);
    void this.reloadKeepingEdits();
  }

  /** Перечитывает карточку после правки источников, не теряя набранные правки черновика. */
  private async reloadKeepingEdits(): Promise<void> {
    try {
      if (this.mode === 'entity') {
        this.entity.set(await this.api.getReviewEntity(this.kind, this.id));
      } else {
        const fresh = await this.api.getReviewItem(this.kind, this.id);
        this.item.update((cur) => (cur ? { ...cur, sources: fresh.sources, cache: fresh.cache, twins: fresh.twins } : fresh));
      }
    } catch {
      // Не критично: набор источников обновится при следующем открытии карточки.
    }
  }

  // ------------------------------------------------------------------ действия очереди

  private emitActed(type: ReviewActed['type']): void {
    this.acted.emit({ type, kind: this.kind, id: this.id, stage: this.item()?.stage ?? null });
  }

  private failure(e: unknown, fallback: string): void {
    if (e instanceof ApiError && e.status === 409) {
      this.toast.error('Задача уже обработана — обновите очередь.');
    } else {
      this.toast.error(e instanceof ApiError && e.detail ? e.detail : fallback);
    }
  }

  /** Горячая клавиша A: поиск — одобрить с выбранным режимом; результат — одобрить черновик как есть. */
  async approve(): Promise<void> {
    if (this.mode !== 'item' || this.busy()) return;
    const item = this.item();
    if (!item) return;

    this.busy.set(true);
    try {
      if (item.stage === 'search') {
        const c = this.choice();
        const queryChanged = this.queryText().trim() !== item.queryText;
        await this.api.approveReviewSearch(this.kind, this.id, {
          queryText: c.type === 'paid' && queryChanged ? this.queryText().trim() : null,
          mode: c.type === 'paid' ? 'search' : 'use-cache',
          twinCacheId: c.type === 'twin' ? c.cacheId : null,
          note: this.note().trim() || null,
        });
        this.toast.success(c.type === 'paid' ? 'Платный поиск одобрен.' : 'Принят набор из кэша — платного поиска не будет.');
      } else {
        await this.api.approveReviewResult(this.kind, this.id, { note: this.note().trim() || null });
        this.toast.success('Результат записан в справочник.');
      }
      this.emitActed('approved');
    } catch (e) {
      this.failure(e, 'Не удалось одобрить.');
    } finally {
      this.busy.set(false);
    }
  }

  /** «Одобрить с правками» — берёт текущие значения редактора, имени и синонимов; изменённые поля лочатся на бэкенде. */
  async approveWithEdits(): Promise<void> {
    if (!this.isResult() || this.busy()) return;
    const payloadJson = this.editor?.currentPayloadJson() ?? null;
    if (this.editor && payloadJson === null) return; // невалидный JSON — ошибка уже показана в редакторе

    this.busy.set(true);
    try {
      await this.api.approveReviewResult(this.kind, this.id, {
        payloadJson,
        displayName: this.displayName().trim() || null,
        aliases: this.aliasesList(),
        note: this.note().trim() || null,
      });
      this.toast.success('Записано в справочник с вашими правками (изменённые поля залочены).');
      this.emitActed('approved');
    } catch (e) {
      this.failure(e, 'Не удалось одобрить с правками.');
    } finally {
      this.busy.set(false);
    }
  }

  /** Горячая клавиша R. */
  async reject(): Promise<void> {
    if (this.mode !== 'item' || this.busy()) return;
    const item = this.item();
    if (!item) return;

    const ok = await this.confirm.confirm({
      title: item.stage === 'search' ? 'Отклонить платный поиск?' : 'Отклонить результат?',
      message: item.stage === 'search'
        ? `«${item.name}» не будет искаться во внешнем поиске, задача завершится как «отклонена администратором».`
        : `Черновик по «${item.name}» не попадёт в справочник, задача завершится как «отклонена администратором».`,
      confirmText: 'Отклонить',
      danger: true,
    });
    if (!ok) return;

    this.busy.set(true);
    try {
      if (item.stage === 'search') await this.api.rejectReviewSearch(this.kind, this.id, null, this.note().trim() || null);
      else await this.api.rejectReviewResult(this.kind, this.id, null, this.note().trim() || null);
      this.toast.info('Отклонено.');
      this.emitActed('rejected');
    } catch (e) {
      this.failure(e, 'Не удалось отклонить.');
    } finally {
      this.busy.set(false);
    }
  }

  /** «Пересуммировать» по текущему набору источников (после правки набора) — без платного поиска. */
  async resummarize(): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    try {
      if (this.mode === 'entity') {
        const preview = await this.api.previewResummarize(this.kind, this.id);
        this.preview.set(preview);
        this.toast.info('Предложение готово — отличия подсвечены, примените его или отбросьте.');
      } else {
        await this.api.resummarizeReviewResult(this.kind, this.id);
        await this.load(true);
        this.toast.success('Черновик пересобран по текущему набору источников.');
      }
      this.sourcesDirty.set(false);
    } catch (e) {
      this.failure(e, 'Не удалось пересуммаризовать.');
    } finally {
      this.busy.set(false);
    }
  }

  skip(): void {
    this.skipped.emit();
  }

  /** Горячая клавиша E: фокус в первое поле редактора (форма payload). */
  edit(): void {
    const host = this.editorHost?.nativeElement;
    if (!host) return;
    host.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    host.querySelector<HTMLElement>('input, textarea')?.focus();
  }

  async saveNote(): Promise<void> {
    const item = this.item();
    if (this.mode !== 'item' || !item || (item.note ?? '') === this.note().trim()) return;
    try {
      await this.api.setReviewNote(this.kind, this.id, this.note().trim() || null);
      this.item.update((cur) => (cur ? { ...cur, note: this.note().trim() || null } : cur));
    } catch {
      this.toast.error('Не удалось сохранить заметку.');
    }
  }

  private aliasesList(): string[] {
    return this.aliasesText().split(',').map((a) => a.trim()).filter((a) => a.length > 0);
  }

  // ------------------------------------------------------------------ карточка записи вне очереди

  /** Применяет предложение пересуммаризации: его payload/имя/синонимы заменяют содержимое редактора (запись в kb не
   * меняется, пока админ не нажмёт «Сохранить» в редакторе). */
  applyPreview(): void {
    const p = this.preview();
    if (!p) return;
    this.editorPayload.set(p.payloadJson);
    this.displayName.set(p.displayName);
    this.aliasesText.set(p.aliases.join(', '));
    this.toast.info('Предложение загружено в редактор — проверьте и сохраните.');
  }

  discardPreview(): void {
    this.preview.set(null);
    const entity = this.entity();
    if (entity) this.editorPayload.set(entity.current.payloadJson);
  }

  /** Сохранение из редактора в карточке записи: тем же путём, что в каталоге (лок изменённых полей, журнал). */
  async saveEntity(event: PayloadSaveEvent): Promise<void> {
    const entity = this.entity();
    if (!entity || this.busy()) return;
    const request = {
      displayName: this.displayName().trim() !== entity.current.displayName ? this.displayName().trim() : null,
      payloadJson: event.payloadJson,
      aliases: this.aliasesList(),
      lockedPayloadKeys: event.changedKeys,
    };
    this.busy.set(true);
    try {
      if (this.kind === 'lab-analyte') await this.api.updateLabAnalyte(entity.current.id, request);
      else await this.api.updateMedication(entity.current.id, request);
      this.toast.success('Запись сохранена, изменённые поля залочены.');
      await this.load();
    } catch (e) {
      this.failure(e, 'Не удалось сохранить запись.');
    } finally {
      this.busy.set(false);
    }
  }

  async markVerified(): Promise<void> {
    const entity = this.entity();
    if (!entity || this.busy()) return;
    this.busy.set(true);
    try {
      if (this.kind === 'lab-analyte') await this.api.markLabAnalyteVerified(entity.current.id);
      else await this.api.markMedicationVerified(entity.current.id);
      this.toast.success('Запись отмечена проверенной.');
      await this.load();
      this.acted.emit({ type: 'verified', kind: this.kind, id: this.id, stage: null });
    } catch (e) {
      this.failure(e, 'Не удалось отметить проверенной.');
    } finally {
      this.busy.set(false);
    }
  }
}
