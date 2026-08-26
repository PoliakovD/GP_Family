import { Component, OnDestroy, OnInit, effect, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import { TelegramService } from '../../services/telegram.service';
import { FamilyStateService } from '../../services/family-state.service';
import { AuthService } from '../../services/auth.service';
import {
  ExtractionJobStatus, ExtractionStage, ExtractionStatus, IndicatorFlag, MedicalRecordKind, RefSource, SpecimenType,
} from '../../models/types';
import type {
  Attachment,
  AttachmentLimits,
  ExtractionStatusResponse,
  IndicatorDto,
  MedicalRecord,
  MedicalRecordFilter,
  RecordSummaryResponse,
  UpdateIndicatorRequest,
  UpdateMedicalRecordRequest,
  UserSpecimen,
  VisitConclusion,
} from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';
import { ExpandableComponent } from '../../shared/expandable/expandable.component';
import { PipelineProgressComponent, PipelineStep } from '../../shared/pipeline-progress/pipeline-progress.component';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { formatPersonName } from '../../shared/util/person-name';
import { SPECIMEN_OPTIONS, specimenLabel } from '../../shared/util/specimen';

/** Терминальные статусы задачи распознавания — опрос останавливается. */
const EXTRACTION_TERMINAL_STATUSES: number[] = [
  ExtractionJobStatus.Completed, ExtractionJobStatus.Failed, ExtractionJobStatus.Skipped,
];

const EXTRACTION_POLL_INTERVAL_MS = 1500;
const SEARCH_DEBOUNCE_MS = 300;
/** Сколько ждать после Completed, прежде чем убрать живой прогресс — успевает мигнуть галочка
 * «Готово», не исчезает мгновенно. */
const PIPELINE_CLEAR_DELAY_MS = 2500;

// Информативнее прежних коротких подписей ("Распознаём"/"Извлекаем данные") — пользователь просил
// видеть, что именно сейчас происходит на каждом шаге, а не общие слова.
const STAGE_LABEL: Partial<Record<number, string>> = {
  [ExtractionStage.Queued]: 'В очереди',
  [ExtractionStage.Decoding]: 'Открываем файл',
  [ExtractionStage.Ocr]: 'Распознаём текст',
  [ExtractionStage.Structuring]: 'Считываем показатели',
  [ExtractionStage.Linking]: 'Сверяем со справочником показателей',
  [ExtractionStage.Summarizing]: 'Готовим резюме анализа',
};

interface KindLabels {
  addButtonLabel: string;
  doctorPlaceholder: string;
  descriptionPlaceholder: string;
  searchPlaceholder: string;
  emptyLabel: string;
}

/** Подписи различаются по виду записи — тот же идиом, что TYPE_LABEL/TYPE_ICON в home.component.ts. */
const KIND_LABELS: Record<MedicalRecordKind, KindLabels> = {
  [MedicalRecordKind.Analysis]: {
    addButtonLabel: 'Добавить запись',
    doctorPlaceholder: 'Врач (необязательно)',
    descriptionPlaceholder: 'Описание (необязательно)',
    searchPlaceholder: 'Поиск по анализам…',
    emptyLabel: 'Записей нет.',
  },
  [MedicalRecordKind.DoctorVisit]: {
    addButtonLabel: 'Добавить посещение',
    doctorPlaceholder: 'Врач / специальность',
    descriptionPlaceholder: 'Заключение (необязательно)',
    searchPlaceholder: 'Поиск по посещениям…',
    emptyLabel: 'Посещений нет.',
  },
};

/** Токен для GET /api/medical-records?kind=. */
const LIST_KIND_TOKEN: Record<MedicalRecordKind, 'analysis' | 'visit'> = {
  [MedicalRecordKind.Analysis]: 'analysis',
  [MedicalRecordKind.DoctorVisit]: 'visit',
};

/** Одна опция дропдауна "Кто пациент?" — либо «Я» (оба id null), либо подопечный семьи, либо
 * другой активный участник. Составной строковый key нужен для [(ngModel)] на <select>. */
interface PatientOption {
  key: string;
  familyDependentId: string | null;
  targetUserId: string | null;
  label: string;
}

const SELF_OPTION: PatientOption = { key: 'self', familyDependentId: null, targetUserId: null, label: 'Я' };

/** Файл, ожидающий загрузки — либо ещё не отправленный (форма создания записи), либо уже
 * прикреплённый к существующей. previewUrl — только для картинок (см. medications-panel.photos). */
interface StagedFile {
  file: File;
  previewUrl: string | null;
}

let nextInstanceId = 0;

/**
 * Panel (не Page — своего URL нет, контекст приходит через input): список записей одного вида
 * (анализ/посещение врача — MedicalRecordKind), форма создания, серверные фильтры + пагинация,
 * шторка «Доступ», вложения. Переиспользуется двумя тонкими Page-обёртками: medical-records-tab
 * («Анализы») и doctor-visits-tab («Врачи») — см. .claude/patterns/frontend_web.md про таксономию
 * Page/Panel.
 *
 * UX-редизайн: форма создания скрыта за «+ Добавить» (была всегда развёрнута над списком),
 * список серверно фильтруется/пагинируется (было — голый список без сортировки), live-прогресс
 * распознавания вместо статичной строки.
 */
@Component({
  selector: 'app-medical-records-panel',
  standalone: true,
  imports: [
    FormsModule, LoadingSpinnerComponent, BottomSheetComponent, SearchFieldComponent,
    ExpandableComponent, PipelineProgressComponent,
  ],
  templateUrl: './medical-records-panel.component.html',
  styleUrl: './medical-records-panel.component.scss',
})
export class MedicalRecordsPanelComponent implements OnInit, OnDestroy {
  readonly kind = input.required<MedicalRecordKind>();

  readonly state = inject(FamilyStateService);
  private readonly api = inject(ApiService);
  private readonly tg = inject(TelegramService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmService);

  /** Доступен в шаблоне для сравнения с this.kind(). */
  readonly Kind = MedicalRecordKind;
  readonly ExtractionJobStatus = ExtractionJobStatus;
  readonly IndicatorFlag = IndicatorFlag;
  readonly stageLabel = STAGE_LABEL;

  /** Зеркало FamilyHub.Infrastructure.Documents.DocumentContentTypes.All — то, что конвейер
   * умеет распознать (плюс .doc — хранится, но не распознаётся, см. докстринг там же). */
  readonly acceptedFileTypes = [
    'image/jpeg', 'image/png', 'image/webp', 'image/heic',
    'application/pdf', 'application/msword',
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    'application/vnd.ms-excel', 'text/csv', 'text/plain', 'application/rtf', 'text/html',
  ].join(',');

  /** Уникален на инстанс — «Анализы» и «Врачи» держат каждый свой экземпляр панели, а
   * <label for> должен указывать ровно на "свой" скрытый file input формы создания. */
  readonly createFileInputId = `medical-record-create-file-input-${nextInstanceId}`;
  readonly doctorsDatalistId = `medical-record-doctors-datalist-${nextInstanceId++}`;

  items: MedicalRecord[] = [];
  loading = true;
  error: string | null = null;

  // --- Пагинация (UX-редизайн: раньше список отдавался целиком, без сортировки) ---
  page = 1;
  readonly pageSize = 15;
  totalCount = 0;
  totalPages = 0;

  // --- Фильтры (UX-редизайн) — серверные, любое изменение сбрасывает страницу на 1. ---
  filtersOpen = false;
  filters = { from: '', to: '', patientKey: 'all', doctor: '' };
  searchQuery = '';
  private searchDebounceHandle: ReturnType<typeof setTimeout> | null = null;

  // --- Форма создания (UX-редизайн: скрыта за «+ Добавить», раньше всегда развёрнута) ---
  createOpen = false;
  saving = false;
  form = {
    recordDate: todayIso(),
    doctor: '',
    description: '',
    familyDependentId: null as string | null,
    targetUserId: null as string | null,
  };
  /** Автоподсказка «Врач» (v2) — доктора, которых пользователь уже вводил в своих записях;
   * грузится один раз, независимо от вида записи (общий пул для «Анализов» и «Врачей»). */
  doctorSuggestions: string[] = [];
  /** Файлы, выбранные в форме создания ДО того, как запись сохранена — грузятся сразу после
   * успешного handleSubmit (у POST /api/medical-records/{id}/attachments нет смысла без recordId). */
  pendingFiles: StagedFile[] = [];

  // Вложения — лениво, по первому раскрытию «Файлы» на карточке (UX-редизайн: раньше грузились
  // для ВСЕХ записей страницы сразу в refresh(), самый большой источник N+1).
  attachmentsByRecord: Record<string, Attachment[]> = {};
  private readonly attachmentsLoadedFor = new Set<string>();
  attachmentLimits: AttachmentLimits | null = null;
  /** Id записи, к которой сейчас идёт загрузка файла — раньше был один булев на всю панель
   * (спиннер «Загружаем…» рисовался в КАЖДОЙ карточке одновременно). */
  uploadingRecordId: string | null = null;

  // Распознавание — результат живёт на уровне ЗАПИСИ (не вложения): повторное распознавание
  // любого вложения записи полностью заменяет предыдущие показатели/резюме этой записи (см.
  // MedicalDocumentExtractionProcessor). Индикаторы/резюме/заключение грузятся сразу в refresh()
  // для Ready-записей ТЕКУЩЕЙ СТРАНИЦЫ (не более pageSize, было — не более общего числа записей).
  extractionStatusByRecord: Record<string, ExtractionStatusResponse | null> = {};
  indicatorsByRecord: Record<string, IndicatorDto[]> = {};
  summaryByRecord: Record<string, RecordSummaryResponse | null> = {};
  conclusionByRecord: Record<string, VisitConclusion | null> = {};
  /** Id записи, для которой сейчас идёт запрос «Распознать» — дизейблит кнопку именно этой записи. */
  recognizingRecordId: string | null = null;
  /** Живой список шагов на карточку (UX-редизайн) — история, не только текущая стадия, см.
   * shared/pipeline-progress. */
  pipelineStepsByRecord: Record<string, PipelineStep[]> = {};
  private readonly pollHandles = new Map<string, ReturnType<typeof setInterval>>();
  private readonly pipelineClearHandles = new Map<string, ReturnType<typeof setTimeout>>();

  // --- Правка/добавление показателя вручную (ошибка OCR, v2 + UX-редизайн) ---
  readonly SpecimenType = SpecimenType;
  readonly RefSource = RefSource;
  readonly specimenOptions = SPECIMEN_OPTIONS;
  editingIndicatorId: string | null = null;
  editIndicatorForm: UpdateIndicatorRequest = emptyIndicatorEdit();
  savingIndicator = false;
  /** Id записи, для которой сейчас открыта строка «+ Добавить показатель» (null — закрыта). */
  creatingIndicatorRecordId: string | null = null;
  newIndicatorForm: UpdateIndicatorRequest = emptyIndicatorEdit();
  savingNewIndicator = false;

  // --- Кастомный биоматериал (UX-редизайн) — свой справочник + LLM-валидация при создании. ---
  customSpecimens: UserSpecimen[] = [];
  addingCustomSpecimen = false;
  customSpecimenInput = '';
  customSpecimenError: string | null = null;
  savingCustomSpecimen = false;

  // L1: семьи, которым владелец глобально расшарил записи (общее для обоих видов — единый шаринг).
  shares: string[] = [];

  // Запись, для которой сейчас открыта шторка «Доступ» (null — шторка закрыта).
  accessRecord: MedicalRecord | null = null;

  // --- Правка даты/врача/описания записи (кнопка «Редактировать», UX-редизайн) ---
  editRecord: MedicalRecord | null = null;
  editRecordForm: UpdateMedicalRecordRequest = { recordDate: '', doctor: '', description: '' };
  savingRecord = false;

  // undefined — ещё ни разу не загружали.
  private loadedKind: MedicalRecordKind | undefined = undefined;

  constructor() {
    // Реагирует на смену вида, пока панель смонтирована (сейчас оба вида монтируются на разных
    // страницах, но контракт Panel требует этого независимо — см. medkits-panel.component.ts).
    effect(() => {
      const kind = this.kind();
      if (kind === this.loadedKind) return;
      this.resetForm();
      this.resetFilters();
      this.accessRecord = null;
      this.page = 1;
      void this.refresh();
    });
  }

  ngOnInit(): void {
    // Первичная загрузка — здесь, а не только в effect(): effect выполняется на следующем цикле
    // change detection и может не успеть отработать до первого рендера шаблона.
    if (this.kind() !== this.loadedKind) {
      void this.refresh();
    }
    if (!this.attachmentLimits) {
      void this.api.getAttachmentLimits().then((limits) => (this.attachmentLimits = limits));
    }
    if (this.doctorSuggestions.length === 0) {
      void this.api.getDoctorSuggestions().then((doctors) => (this.doctorSuggestions = doctors));
    }
    if (this.customSpecimens.length === 0) {
      void this.api.getSpecimens().then((s) => (this.customSpecimens = s));
    }
  }

  /** Опрос статуса распознавания использует setInterval — без явной остановки таймеры
   * пережили бы размонтирование панели (переключение вкладки Health-хаба). */
  ngOnDestroy(): void {
    for (const handle of this.pollHandles.values()) clearInterval(handle);
    this.pollHandles.clear();
    for (const handle of this.pipelineClearHandles.values()) clearTimeout(handle);
    this.pipelineClearHandles.clear();
    if (this.searchDebounceHandle) clearTimeout(this.searchDebounceHandle);
    this.clearPendingFiles();
  }

  get labels(): KindLabels {
    return KIND_LABELS[this.kind()];
  }

  /** «Я» + все подопечные и все другие активные участники из моих активных семей (дедуп по
   * userId — участник может состоять сразу в нескольких общих со мной семьях). */
  get patientOptions(): PatientOption[] {
    const options: PatientOption[] = [SELF_OPTION];
    const myUserId = this.auth.me()?.userId;
    const seenUserIds = new Set<string>(myUserId ? [myUserId] : []);

    for (const family of this.state.activeFamilies()) {
      for (const dep of family.dependents ?? []) {
        options.push({
          key: `dep:${dep.id}`,
          familyDependentId: dep.id,
          targetUserId: null,
          label: `${dep.isPet ? dep.firstName : formatPersonName(dep, 'full')} (${family.name})`,
        });
      }
      for (const member of family.currentMembers ?? []) {
        if (seenUserIds.has(member.id)) continue;
        seenUserIds.add(member.id);
        options.push({
          key: `user:${member.id}`,
          familyDependentId: null,
          targetUserId: member.id,
          label: `${formatPersonName(member, 'full')} (${family.name})`,
        });
      }
    }
    return options;
  }

  /** Фильтр «Пациент» — те же опции + «Все» сверху (форма создания «Все» не предлагает,
   * там пациент обязателен). */
  get filterPatientOptions(): PatientOption[] {
    return [{ key: 'all', familyDependentId: null, targetUserId: null, label: 'Все' }, ...this.patientOptions];
  }

  get selectedPatientKey(): string {
    if (this.form.familyDependentId) return `dep:${this.form.familyDependentId}`;
    if (this.form.targetUserId) return `user:${this.form.targetUserId}`;
    return 'self';
  }

  /** v2: пациент — только выбор из self/подопечный/участник семьи, без свободного текстового
   * поля (личность полностью выражается familyDependentId/targetUserId, имя резолвится на
   * чтение из профиля — см. MedicalRecordService.ResolvePersonNamesAsync). */
  set selectedPatientKey(key: string) {
    const option = this.patientOptions.find((o) => o.key === key);
    this.form.familyDependentId = option?.familyDependentId ?? null;
    this.form.targetUserId = option?.targetUserId ?? null;
  }

  // --- Фильтры/поиск/пагинация ---

  /** Сколько фильтров сейчас активно — счётчик на заголовке кнопки «Фильтры». */
  get activeFilterCount(): number {
    let count = 0;
    if (this.filters.from) count++;
    if (this.filters.to) count++;
    if (this.filters.patientKey !== 'all') count++;
    if (this.filters.doctor.trim()) count++;
    return count;
  }

  onFilterChange(): void {
    this.page = 1;
    void this.refresh();
  }

  resetFilters(): void {
    this.filters = { from: '', to: '', patientKey: 'all', doctor: '' };
    this.searchQuery = '';
    this.page = 1;
    void this.refresh();
  }

  onSearchQueryChange(value: string): void {
    this.searchQuery = value;
    if (this.searchDebounceHandle) clearTimeout(this.searchDebounceHandle);
    this.searchDebounceHandle = setTimeout(() => {
      this.page = 1;
      void this.refresh();
    }, SEARCH_DEBOUNCE_MS);
  }

  get canGoPrev(): boolean {
    return this.page > 1;
  }

  get canGoNext(): boolean {
    return this.page < this.totalPages;
  }

  goToPage(page: number): void {
    if (page < 1 || page > this.totalPages || page === this.page) return;
    this.page = page;
    void this.refresh();
  }

  private buildFilter(): MedicalRecordFilter {
    const opt = this.filters.patientKey === 'all' || this.filters.patientKey === 'self'
      ? null
      : this.patientOptions.find((o) => o.key === this.filters.patientKey);
    return {
      kind: LIST_KIND_TOKEN[this.kind()],
      from: this.filters.from || undefined,
      to: this.filters.to || undefined,
      dependentId: opt?.familyDependentId ?? undefined,
      targetUserId: opt?.targetUserId ?? undefined,
      self: this.filters.patientKey === 'self' ? true : undefined,
      doctor: this.filters.doctor.trim() || undefined,
      q: this.searchQuery.trim() || undefined,
      page: this.page,
      pageSize: this.pageSize,
    };
  }

  async refresh(): Promise<void> {
    const kind = this.kind();
    this.loadedKind = kind;
    this.loading = true;
    try {
      const [page, shares] = await Promise.all([
        this.api.getMedicalRecords(this.buildFilter()),
        this.api.getMedicalRecordShares(),
      ]);
      this.items = page.items;
      this.totalCount = page.totalCount;
      this.totalPages = page.totalPages;
      this.shares = shares;
      // Открытая шторка должна остаться синхронной с перезагруженным состоянием записи.
      if (this.accessRecord) {
        this.accessRecord = this.items.find((r) => r.id === this.accessRecord!.id) ?? null;
      }
      this.error = null;

      // Показатели/резюме/заключение — только для готовых записей ТЕКУЩЕЙ страницы (≤15), не
      // для всего списка сразу (UX-редизайн — было главным источником N+1 вместе со вложениями).
      await Promise.all(
        this.items
          .filter((item) => item.extractionStatus === ExtractionStatus.Ready)
          .map((item) => this.loadExtractionResult(item)),
      );
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить данные.';
    } finally {
      this.loading = false;
    }
  }

  async handleSubmit(): Promise<void> {
    if (!this.form.recordDate || this.saving) return;
    this.saving = true;
    try {
      const created = await this.api.createMedicalRecord({
        kind: this.kind(),
        recordDate: this.form.recordDate,
        doctor: this.form.doctor.trim() || null,
        description: this.form.description.trim() || null,
        hideFromFamilyIds: null,
        familyDependentId: this.form.familyDependentId,
        targetUserId: this.form.targetUserId,
      });

      // Файлы, выбранные ДО сохранения формы — грузим сразу вслед, чтобы не заставлять
      // пользователя искать свежесозданную запись в списке и повторно нажимать «Прикрепить».
      let uploadFailed = 0;
      if (this.pendingFiles.length > 0) {
        this.uploadingRecordId = created.id;
        try {
          for (const staged of this.pendingFiles) {
            try {
              await this.api.uploadAttachment(created.id, staged.file);
            } catch {
              uploadFailed++;
            }
          }
        } finally {
          this.uploadingRecordId = null;
        }
      }

      this.resetForm();
      this.createOpen = false;
      this.page = 1;
      await this.refresh();
      this.error = uploadFailed > 0 ? `Запись сохранена, но ${uploadFailed} файлов не загрузилось — прикрепите их к записи ниже.` : null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось сохранить запись.';
    } finally {
      this.saving = false;
    }
  }

  // --- Файлы формы создания (до сохранения записи) ---

  canAddMorePendingFiles(): boolean {
    return !this.attachmentLimits || this.pendingFiles.length < this.attachmentLimits.maxFilesPerRecord;
  }

  onPendingFilesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';
    if (files.length === 0) return;

    const { accepted, skippedByCount, tooLarge } = this.filterAgainstLimits(this.pendingFiles.length, files);
    for (const file of accepted) {
      this.pendingFiles.push({ file, previewUrl: file.type.startsWith('image/') ? URL.createObjectURL(file) : null });
    }

    const problems: string[] = [];
    if (skippedByCount > 0 && this.attachmentLimits) {
      problems.push(`не добавлено ${skippedByCount} файлов сверх лимита (${this.attachmentLimits.maxFilesPerRecord} на запись)`);
    }
    if (tooLarge.length > 0 && this.attachmentLimits) {
      problems.push(`${tooLarge.length} файлов превышают ${formatMb(this.attachmentLimits.maxFileSizeBytes)} и не добавлены`);
    }
    this.error = problems.length > 0 ? `${problems.join(', ')}.` : null;
  }

  removePendingFile(index: number): void {
    const [removed] = this.pendingFiles.splice(index, 1);
    if (removed?.previewUrl) URL.revokeObjectURL(removed.previewUrl);
  }

  private clearPendingFiles(): void {
    this.pendingFiles.forEach((f) => f.previewUrl && URL.revokeObjectURL(f.previewUrl));
    this.pendingFiles = [];
  }

  /** Общая клиентская предвалидация для формы создания и для дозагрузки к существующей записи —
   * лимиты те же (AttachmentUploadOptions), сервер всё равно перепроверит независимо. */
  private filterAgainstLimits(existingCount: number, files: File[]): { accepted: File[]; skippedByCount: number; tooLarge: File[] } {
    const limits = this.attachmentLimits;
    if (!limits) return { accepted: files, skippedByCount: 0, tooLarge: [] };

    const room = Math.max(0, limits.maxFilesPerRecord - existingCount);
    const withinCount = files.slice(0, room);
    const tooLarge = withinCount.filter((f) => f.size > limits.maxFileSizeBytes);
    const accepted = withinCount.filter((f) => f.size <= limits.maxFileSizeBytes);
    return { accepted, skippedByCount: files.length - withinCount.length, tooLarge };
  }

  /** Безусловное удаление доступно только владельцу (кто физически загрузил) — сервер
   * перепроверит независимо от того, кому запись сейчас видна. */
  canDelete(record: MedicalRecord): boolean {
    return record.ownerUserId === this.auth.me()?.userId;
  }

  async handleDelete(record: MedicalRecord): Promise<void> {
    const confirmed = await this.confirm.confirm({
      title: 'Удалить запись?',
      message: 'Запись и все её вложения будут удалены безвозвратно.',
      confirmText: 'Удалить',
      danger: true,
    });
    if (!confirmed) return;

    try {
      await this.api.deleteMedicalRecord(record.id);
      if (this.accessRecord?.id === record.id) this.accessRecord = null;
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось удалить запись.';
    }
  }

  // --- Файлы карточки (лениво — при первом раскрытии «Файлы», UX-редизайн) ---

  /** Раскрытие/сворачивание «Файлы» на карточке — грузит вложения только один раз. */
  async onFilesToggle(record: MedicalRecord, open: boolean): Promise<void> {
    if (!open || this.attachmentsLoadedFor.has(record.id)) return;
    this.attachmentsLoadedFor.add(record.id);
    try {
      const attachments = await this.api.getRecordAttachments(record.id);
      this.attachmentsByRecord = { ...this.attachmentsByRecord, [record.id]: attachments };
    } catch (err) {
      this.attachmentsLoadedFor.delete(record.id);
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить вложения.';
    }
  }

  /** Сколько ещё файлов можно приложить к этой записи — null, пока список вложений ещё не
   * загружен (карточка ни разу не раскрывалась) ЛИБО лимиты ещё не пришли. */
  remainingSlots(recordId: string): number | null {
    if (!this.attachmentLimits || !this.attachmentsLoadedFor.has(recordId)) return null;
    return Math.max(0, this.attachmentLimits.maxFilesPerRecord - this.attachmentsFor(recordId).length);
  }

  canAddMoreAttachments(recordId: string): boolean {
    return this.remainingSlots(recordId) !== 0;
  }

  /** До 8 файлов за раз (multiple на инпуте) — загружаются последовательно (сервер принимает один
   * файл за запрос), список вложений и остаток слотов обновляются по мере успеха каждого. */
  async handleUpload(record: MedicalRecord, event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = ''; // позволяет выбрать те же файлы повторно
    if (files.length === 0) return;

    const recordId = record.id;
    this.attachmentsLoadedFor.add(recordId); // на случай, если «Файлы» ещё не раскрывали
    const { accepted, skippedByCount, tooLarge } = this.filterAgainstLimits(this.attachmentsFor(recordId).length, files);

    this.uploadingRecordId = recordId;
    let failed = 0;
    try {
      for (const file of accepted) {
        try {
          const attachment = await this.api.uploadAttachment(recordId, file);
          this.attachmentsByRecord = {
            ...this.attachmentsByRecord,
            [recordId]: [...(this.attachmentsByRecord[recordId] ?? []), attachment],
          };
        } catch {
          failed++;
        }
      }
    } finally {
      this.uploadingRecordId = null;
    }

    // Счётчики на DTO записи (attachmentCount/unrecognizedAttachmentCount) устарели после
    // загрузки — перечитываем список, чтобы кнопка «Распознать» и подпись «Файлы (N)» сошлись.
    await this.refresh();

    const limits = this.attachmentLimits;
    const problems: string[] = [];
    if (skippedByCount > 0 && limits) problems.push(`не прикреплено ${skippedByCount} файлов сверх лимита (${limits.maxFilesPerRecord} на запись)`);
    if (tooLarge.length > 0 && limits) problems.push(`${tooLarge.length} файлов превышают ${formatMb(limits.maxFileSizeBytes)} и не отправлены`);
    if (failed > 0) problems.push(`${failed} файлов не загрузились`);
    this.error = problems.length > 0 ? `Загрузка завершена частично: ${problems.join(', ')}.` : null;
  }

  // --- Распознавание (кнопка «Распознать» на записи, v2 — обрабатывает все ещё не
  // распознанные вложения последовательно за один прогон, не по клику на каждый файл) ---

  /** Видимость кнопки «Распознать» — по счётчику из DTO, БЕЗ загрузки списка вложений. */
  hasUnrecognizedAttachments(record: MedicalRecord): boolean {
    return record.unrecognizedAttachmentCount > 0;
  }

  async handleRecognize(record: MedicalRecord): Promise<void> {
    this.recognizingRecordId = record.id;
    this.clearPipelineTimer(record.id);
    this.pipelineStepsByRecord = {
      ...this.pipelineStepsByRecord,
      [record.id]: [{ id: 'queued', label: 'В очереди', state: 'active' }],
    };
    try {
      await this.api.requestExtraction(record.id);
      this.extractionStatusByRecord = { ...this.extractionStatusByRecord, [record.id]: null };
      this.startPolling(record);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось запустить распознавание.';
      this.recognizingRecordId = null;
    }
  }

  private startPolling(record: MedicalRecord): void {
    const existing = this.pollHandles.get(record.id);
    if (existing) clearInterval(existing);

    const tick = async () => {
      try {
        const prev = this.extractionStatusByRecord[record.id] ?? null;
        const status = await this.api.getExtractionStatus(record.id);
        this.extractionStatusByRecord = { ...this.extractionStatusByRecord, [record.id]: status };
        this.updatePipelineSteps(record.id, status, prev);

        if (EXTRACTION_TERMINAL_STATUSES.includes(status.status)) {
          this.stopPolling(record.id);
          this.recognizingRecordId = null;
          if (status.status === ExtractionJobStatus.Completed) {
            await this.loadExtractionResult(record);
            await this.refresh();
          } else if (status.error) {
            this.error = status.error;
          }
          this.schedulePipelineClear(record.id);
        }
      } catch (err) {
        this.stopPolling(record.id);
        this.recognizingRecordId = null;
        this.error = err instanceof ApiError ? err.message : 'Не удалось получить статус распознавания.';
      }
    };

    void tick();
    this.pollHandles.set(record.id, setInterval(() => void tick(), EXTRACTION_POLL_INTERVAL_MS));
  }

  /** Живой список шагов (UX-редизайн) — растущий список «уже сделано» + текущий пульсирующий
   * шаг, не статичная строка. Только выполненные + активный: будущие шаги не показываем, конвейер
   * может их пропустить (текстовый путь не заходит в OCR). */
  private updatePipelineSteps(recordId: string, status: ExtractionStatusResponse, prev: ExtractionStatusResponse | null): void {
    const steps = [...(this.pipelineStepsByRecord[recordId] ?? [])];
    const markLastDone = () => {
      const last = steps[steps.length - 1];
      if (last && last.state === 'active') steps[steps.length - 1] = { ...last, state: 'done' };
    };

    if (status.status === ExtractionJobStatus.Failed || status.status === ExtractionJobStatus.Skipped) {
      markLastDone();
      steps.push({ id: `outcome-${steps.length}`, label: status.error ?? 'Не удалось распознать документ.', state: 'error' });
    } else if (status.status === ExtractionJobStatus.Completed) {
      markLastDone();
      steps.push({ id: `outcome-${steps.length}`, label: 'Готово', state: 'done' });
    } else {
      // Новый обработанный файл — отдельная строка с галочкой, до перехода к следующей стадии.
      if (prev && status.processedFiles > prev.processedFiles) {
        markLastDone();
        steps.push({ id: `file-${status.processedFiles}`, label: `Файл ${status.processedFiles} распознан`, state: 'done' });
      }
      if (!prev || prev.stage !== status.stage || steps.length === 0) {
        markLastDone();
        const base = this.stageLabel[status.stage] ?? 'Обрабатываем…';
        const label = status.totalFiles > 1 ? `${base} — файл ${this.currentFileNumber(status)} из ${status.totalFiles}` : base;
        steps.push({ id: `stage-${status.stage}-${steps.length}`, label, state: 'active' });
      }
    }

    this.pipelineStepsByRecord = { ...this.pipelineStepsByRecord, [recordId]: steps };
  }

  private schedulePipelineClear(recordId: string): void {
    this.clearPipelineTimer(recordId);
    const handle = setTimeout(() => {
      const { [recordId]: _removed, ...rest } = this.pipelineStepsByRecord;
      this.pipelineStepsByRecord = rest;
      this.pipelineClearHandles.delete(recordId);
    }, PIPELINE_CLEAR_DELAY_MS);
    this.pipelineClearHandles.set(recordId, handle);
  }

  private clearPipelineTimer(recordId: string): void {
    const handle = this.pipelineClearHandles.get(recordId);
    if (handle) {
      clearTimeout(handle);
      this.pipelineClearHandles.delete(recordId);
    }
  }

  private stopPolling(recordId: string): void {
    const handle = this.pollHandles.get(recordId);
    if (handle) {
      clearInterval(handle);
      this.pollHandles.delete(recordId);
    }
  }

  private async loadExtractionResult(record: MedicalRecord): Promise<void> {
    try {
      if (record.kind === MedicalRecordKind.Analysis) {
        const [indicators, summary] = await Promise.all([
          this.api.getRecordIndicators(record.id),
          this.api.getRecordSummary(record.id).catch(() => null),
        ]);
        this.indicatorsByRecord = { ...this.indicatorsByRecord, [record.id]: indicators };
        this.summaryByRecord = { ...this.summaryByRecord, [record.id]: summary };
      } else {
        const conclusion = await this.api.getRecordConclusion(record.id).catch(() => null);
        this.conclusionByRecord = { ...this.conclusionByRecord, [record.id]: conclusion };
      }
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить результат распознавания.';
    }
  }

  indicatorsFor(recordId: string): IndicatorDto[] {
    return this.indicatorsByRecord[recordId] ?? [];
  }

  flagClass(flag: number): string {
    switch (flag) {
      case IndicatorFlag.Low:
      case IndicatorFlag.High:
        return 'indicator-flag-warning';
      case IndicatorFlag.Critical:
        return 'indicator-flag-danger';
      case IndicatorFlag.Normal:
        return 'indicator-flag-ok';
      default:
        return 'indicator-flag-unknown';
    }
  }

  flagLabel(flag: number): string {
    switch (flag) {
      case IndicatorFlag.Low: return 'ниже нормы';
      case IndicatorFlag.High: return 'выше нормы';
      case IndicatorFlag.Critical: return 'критично';
      case IndicatorFlag.Normal: return 'норма';
      default: return '';
    }
  }

  indicatorReference(indicator: IndicatorDto): string | null {
    if (indicator.refText) return indicator.refText;
    if (indicator.refLowText && indicator.refHighText) return `${indicator.refLowText}–${indicator.refHighText}`;
    if (indicator.refHighText) return `< ${indicator.refHighText}`;
    if (indicator.refLowText) return `> ${indicator.refLowText}`;
    return null;
  }

  /** «Файл N из totalFiles» в процессе распознавания — processedFiles уже завершены, текущий —
   * следующий по счёту (капнуто totalFiles на случай отставания статуса от факта). */
  currentFileNumber(status: ExtractionStatusResponse): number {
    return Math.min(status.processedFiles + 1, status.totalFiles);
  }

  /** Короткое имя показателя для строки таблицы — нормализованный analyteKey с заглавной буквы
   * (UX-редизайн: полное имя из бланка показывается только при раскрытии строки). */
  shortIndicatorName(indicator: IndicatorDto): string {
    const key = indicator.analyteKey.trim();
    return key.length > 0 ? key[0].toUpperCase() + key.slice(1) : indicator.displayName;
  }

  /** Комбинированный ключ (specimen[:customId]) для одиночного <select> формы правки/создания —
   * нативный <option> не даёт слушать (click) внутри <select> кроссбраузерно, поэтому системные и
   * кастомные значения кодируются одной строкой, а не отдельным обработчиком клика по опции. */
  specimenKey(form: UpdateIndicatorRequest): string {
    return form.specimen === SpecimenType.Other && form.specimenCustomId
      ? `${form.specimen}:${form.specimenCustomId}`
      : `${form.specimen}`;
  }

  applySpecimenKey(form: UpdateIndicatorRequest, key: string): void {
    const [specimenPart, customId] = key.split(':');
    form.specimen = Number(specimenPart) as SpecimenType;
    form.specimenCustomId = customId ?? null;
  }

  specimenLabelFor(indicator: { specimen: number; specimenCustomId: string | null }): string {
    if (indicator.specimen === SpecimenType.Other && indicator.specimenCustomId) {
      const custom = this.customSpecimens.find((c) => c.id === indicator.specimenCustomId);
      if (custom) return custom.displayName;
    }
    return specimenLabel(indicator.specimen);
  }

  /** Бэйдж «рассчитано ИИ» — только для диапазона, посчитанного локальной LLM по методике из
   * справочника (каскад п.1a, RefSource.KbCalculated), не для фиксированного диапазона/бланка. */
  isCalculatedRef(indicator: IndicatorDto): boolean {
    return indicator.refSource === RefSource.KbCalculated;
  }

  // --- Раскрытая строка показателя (полное имя из бланка + подробности) ---

  expandedIndicatorId: string | null = null;

  toggleIndicatorRow(indicator: IndicatorDto): void {
    this.expandedIndicatorId = this.expandedIndicatorId === indicator.id ? null : indicator.id;
  }

  // --- Правка показателя вручную (ошибка OCR, v2) ---

  startEditIndicator(indicator: IndicatorDto): void {
    this.creatingIndicatorRecordId = null;
    this.editingIndicatorId = indicator.id;
    this.editIndicatorForm = {
      displayName: indicator.displayName,
      valueRaw: indicator.valueRaw,
      unit: indicator.unit,
      specimen: indicator.specimen as SpecimenType,
      refLowText: indicator.refLowText,
      refHighText: indicator.refHighText,
      refText: indicator.refText,
      specimenCustomId: indicator.specimenCustomId,
    };
  }

  cancelEditIndicator(): void {
    this.editingIndicatorId = null;
    this.editIndicatorForm = emptyIndicatorEdit();
    this.addingCustomSpecimen = false;
  }

  async saveEditIndicator(recordId: string): Promise<void> {
    if (!this.editingIndicatorId || !this.editIndicatorForm.displayName.trim()) return;
    this.savingIndicator = true;
    try {
      await this.api.updateIndicator(this.editingIndicatorId, sanitizeIndicatorForm(this.editIndicatorForm));
      const indicators = await this.api.getRecordIndicators(recordId);
      this.indicatorsByRecord = { ...this.indicatorsByRecord, [recordId]: indicators };
      this.cancelEditIndicator();
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось сохранить правку — возможно, такой показатель уже есть в записи.';
    } finally {
      this.savingIndicator = false;
    }
  }

  async deleteIndicatorRow(recordId: string, indicator: IndicatorDto): Promise<void> {
    const confirmed = await this.confirm.confirm({
      title: 'Удалить показатель?',
      message: `«${indicator.displayName}» будет удалён из записи безвозвратно.`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!confirmed) return;

    try {
      await this.api.deleteIndicator(indicator.id);
      const indicators = await this.api.getRecordIndicators(recordId);
      this.indicatorsByRecord = { ...this.indicatorsByRecord, [recordId]: indicators };
      if (this.expandedIndicatorId === indicator.id) this.expandedIndicatorId = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось удалить показатель.';
    }
  }

  // --- Ручное добавление показателя (UX-редизайн) ---

  startCreateIndicator(recordId: string): void {
    this.editingIndicatorId = null;
    this.creatingIndicatorRecordId = recordId;
    this.newIndicatorForm = emptyIndicatorEdit();
  }

  cancelCreateIndicator(): void {
    this.creatingIndicatorRecordId = null;
    this.newIndicatorForm = emptyIndicatorEdit();
    this.addingCustomSpecimen = false;
  }

  async saveNewIndicator(): Promise<void> {
    if (!this.creatingIndicatorRecordId || !this.newIndicatorForm.displayName.trim()) return;
    const recordId = this.creatingIndicatorRecordId;
    this.savingNewIndicator = true;
    try {
      await this.api.createIndicator(recordId, sanitizeIndicatorForm(this.newIndicatorForm));
      const indicators = await this.api.getRecordIndicators(recordId);
      this.indicatorsByRecord = { ...this.indicatorsByRecord, [recordId]: indicators };
      await this.refresh();
      this.cancelCreateIndicator();
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось добавить показатель — возможно, такой уже есть в записи.';
    } finally {
      this.savingNewIndicator = false;
    }
  }

  // --- Кастомный биоматериал ---

  startAddCustomSpecimen(): void {
    this.addingCustomSpecimen = true;
    this.customSpecimenInput = '';
    this.customSpecimenError = null;
  }

  cancelAddCustomSpecimen(): void {
    this.addingCustomSpecimen = false;
    this.customSpecimenInput = '';
    this.customSpecimenError = null;
  }

  /** Проверяет и добавляет биоматериал через LLM-валидацию (UserSpecimenService), затем сразу
   * подставляет его в текущую форму (правку или создание — то, что сейчас открыто). */
  async submitCustomSpecimen(): Promise<void> {
    const name = this.customSpecimenInput.trim();
    if (!name || this.savingCustomSpecimen) return;
    this.savingCustomSpecimen = true;
    this.customSpecimenError = null;
    try {
      const created = await this.api.createSpecimen(name);
      if (!this.customSpecimens.some((s) => s.id === created.id)) {
        this.customSpecimens = [...this.customSpecimens, created].sort((a, b) => a.displayName.localeCompare(b.displayName, 'ru'));
      }
      const target = this.creatingIndicatorRecordId ? this.newIndicatorForm : this.editIndicatorForm;
      target.specimen = SpecimenType.Other;
      target.specimenCustomId = created.id;
      this.addingCustomSpecimen = false;
      this.customSpecimenInput = '';
    } catch (err) {
      this.customSpecimenError = err instanceof ApiError ? err.message : 'Не удалось проверить биоматериал.';
    } finally {
      this.savingCustomSpecimen = false;
    }
  }

  async handleOpenAttachment(attachmentId: string): Promise<void> {
    try {
      const { url } = await this.api.getAttachmentUrl(attachmentId);
      this.tg.openExternalLink(url);
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось получить ссылку на файл.';
    }
  }

  attachmentsFor(recordId: string): Attachment[] {
    return this.attachmentsByRecord[recordId] ?? [];
  }

  // --- Доступ (bottom-sheet «Доступ») ---

  openAccessSheet(record: MedicalRecord): void {
    this.accessRecord = record;
  }

  closeAccessSheet(): void {
    this.accessRecord = null;
  }

  // --- Правка записи (bottom-sheet «Редактировать») ---

  openEditSheet(record: MedicalRecord): void {
    this.editRecord = record;
    this.editRecordForm = { recordDate: record.recordDate, doctor: record.doctor ?? '', description: record.description ?? '' };
  }

  closeEditSheet(): void {
    this.editRecord = null;
  }

  async saveEditRecord(): Promise<void> {
    if (!this.editRecord || !this.editRecordForm.recordDate || this.savingRecord) return;
    this.savingRecord = true;
    try {
      await this.api.updateMedicalRecord(this.editRecord.id, {
        recordDate: this.editRecordForm.recordDate,
        doctor: this.editRecordForm.doctor?.trim() || null,
        description: this.editRecordForm.description?.trim() || null,
      });
      this.closeEditSheet();
      await this.refresh();
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось сохранить изменения.';
    } finally {
      this.savingRecord = false;
    }
  }

  /** Видна ли КОНКРЕТНАЯ запись данной семье: (L1 share есть) И (L2 hide нет). */
  isVisibleToFamily(record: MedicalRecord, familyId: string): boolean {
    return this.shares.includes(familyId) && !record.hiddenFamilyIds.includes(familyId);
  }

  /** Видна ли запись хотя бы одной расшаренной семье — определяет активную опцию сегмента. */
  private visibleToAny(record: MedicalRecord): boolean {
    return this.shares.some((fid) => !record.hiddenFamilyIds.includes(fid));
  }

  isOnlyMe(record: MedicalRecord): boolean {
    return this.shares.length === 0 || !this.visibleToAny(record);
  }

  /** Сводка для карточки: «Только вы» / «Все семьи» / «Все семьи, кроме N». */
  accessSummary(record: MedicalRecord): string {
    const total = this.shares.length;
    if (total === 0) return 'Только вы';
    const hiddenCount = this.shares.filter((fid) => record.hiddenFamilyIds.includes(fid)).length;
    if (hiddenCount === total) return 'Только вы';
    if (hiddenCount === 0) return 'Все семьи';
    return `Все семьи, кроме ${hiddenCount}`;
  }

  /**
   * Тумблер одной семьи в шторке. Включение автоматически создаёт L1-шаринг, если его ещё не
   * было — иначе тумблер не мог бы включить видимость семье, которой владелец никогда явно не
   * открывал записи. L1-шаринг общий на оба вида (единый шаринг «Анализы + Врачи»), поэтому это
   * затрагивает базовую видимость всех записей той же семье, а не только текущего вида — осознанно.
   */
  async setFamilyAccess(record: MedicalRecord, familyId: string, visible: boolean): Promise<void> {
    try {
      if (visible) {
        if (!this.shares.includes(familyId)) {
          await this.api.shareMedicalRecord(familyId);
        }
        await this.api.unhideMedicalRecord(record.id, [familyId]);
      } else {
        await this.api.hideMedicalRecord(record.id, [familyId]);
      }
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Действие доступно только владельцу записи.';
    }
  }

  /** Сегмент «Только я / Все семьи» — bulk-скрытие/раскрытие записи для ВСЕХ уже расшаренных семей. */
  async setAccessMode(record: MedicalRecord, onlyMe: boolean): Promise<void> {
    if (this.shares.length === 0) return;
    try {
      if (onlyMe) {
        await this.api.hideMedicalRecord(record.id, this.shares);
      } else {
        await this.api.unhideMedicalRecord(record.id, this.shares);
      }
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Действие доступно только владельцу записи.';
    }
  }

  private resetForm(): void {
    this.form = {
      recordDate: todayIso(),
      doctor: '',
      description: '',
      familyDependentId: null,
      targetUserId: null,
    };
    this.clearPendingFiles();
  }
}

function formatMb(bytes: number): string {
  return `${(bytes / (1024 * 1024)).toFixed(1)} МБ`;
}

/** Сегодняшняя дата в формате input[type=date] — v2: дефолт формы создания, распознавание может
 * позже переопределить её датой, найденной в самом документе (record.recordDate обновится). */
function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}

function emptyIndicatorEdit(): UpdateIndicatorRequest {
  return {
    displayName: '', valueRaw: '', unit: null, specimen: SpecimenType.Unknown,
    refLowText: null, refHighText: null, refText: null, specimenCustomId: null,
  };
}

/** Обрезка пробелов + пустая строка → null — общий шаг перед отправкой формы показателя
 * (правка и создание используют одну и ту же форму). */
function sanitizeIndicatorForm(form: UpdateIndicatorRequest): UpdateIndicatorRequest {
  return {
    ...form,
    displayName: form.displayName.trim(),
    valueRaw: form.valueRaw.trim(),
    unit: form.unit?.trim() || null,
    refLowText: form.refLowText?.trim() || null,
    refHighText: form.refHighText?.trim() || null,
    refText: form.refText?.trim() || null,
    specimenCustomId: form.specimen === SpecimenType.Other ? (form.specimenCustomId ?? null) : null,
  };
}
