import { Component, OnDestroy, OnInit, effect, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import { TelegramService } from '../../services/telegram.service';
import { FamilyStateService } from '../../services/family-state.service';
import { AuthService } from '../../services/auth.service';
import { ExtractionJobStatus, ExtractionStage, ExtractionStatus, IndicatorFlag, MedicalRecordKind } from '../../models/types';
import type {
  Attachment,
  AttachmentLimits,
  ExtractionStatusResponse,
  IndicatorDto,
  MedicalRecord,
  RecordSummaryResponse,
  SearchResultItem,
  VisitConclusion,
} from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { DebouncedSearch, SEARCH_MIN_QUERY_LENGTH } from '../../shared/util/debounced-search';

/** Терминальные статусы задачи распознавания — опрос останавливается. */
const EXTRACTION_TERMINAL_STATUSES: number[] = [
  ExtractionJobStatus.Completed, ExtractionJobStatus.Failed, ExtractionJobStatus.Skipped,
];

const EXTRACTION_POLL_INTERVAL_MS = 1500;

const STAGE_LABEL: Partial<Record<number, string>> = {
  [ExtractionStage.Queued]: 'В очереди',
  [ExtractionStage.Decoding]: 'Открываем файл',
  [ExtractionStage.Ocr]: 'Распознаём',
  [ExtractionStage.Structuring]: 'Извлекаем данные',
  [ExtractionStage.Linking]: 'Сверяем со справочником',
  [ExtractionStage.Summarizing]: 'Готовим резюме',
};

interface KindLabels {
  addButtonLabel: string;
  personPlaceholder: string;
  doctorPlaceholder: string;
  descriptionPlaceholder: string;
  searchPlaceholder: string;
  emptyLabel: string;
}

/** Подписи различаются по виду записи — тот же идиом, что TYPE_LABEL/TYPE_ICON в home.component.ts. */
const KIND_LABELS: Record<MedicalRecordKind, KindLabels> = {
  [MedicalRecordKind.Analysis]: {
    addButtonLabel: 'Добавить запись',
    personPlaceholder: 'Пациент',
    doctorPlaceholder: 'Врач (необязательно)',
    descriptionPlaceholder: 'Описание (необязательно)',
    searchPlaceholder: 'Поиск по анализам…',
    emptyLabel: 'Записей нет.',
  },
  [MedicalRecordKind.DoctorVisit]: {
    addButtonLabel: 'Добавить посещение',
    personPlaceholder: 'Пациент',
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

/** Токен для GET /api/search?types= (SearchDtos.SearchResultType, регистр не важен). */
const SEARCH_TYPE_TOKEN: Record<MedicalRecordKind, string> = {
  [MedicalRecordKind.Analysis]: 'record',
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
 * (анализ/посещение врача — MedicalRecordKind), форма создания, поиск, шторка «Доступ», вложения.
 * Переиспользуется двумя тонкими Page-обёртками: medical-records-tab («Анализы») и
 * doctor-visits-tab («Врачи») — см. .claude/patterns/frontend_web.md про таксономию Page/Panel.
 */
@Component({
  selector: 'app-medical-records-panel',
  standalone: true,
  imports: [FormsModule, LoadingSpinnerComponent, BottomSheetComponent, SearchFieldComponent],
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
  readonly createFileInputId = `medical-record-create-file-input-${nextInstanceId++}`;

  items: MedicalRecord[] = [];
  form = {
    personName: '',
    recordDate: '',
    doctor: '',
    description: '',
    familyDependentId: null as string | null,
    targetUserId: null as string | null,
  };
  /** Файлы, выбранные в форме создания ДО того, как запись сохранена — грузятся сразу после
   * успешного handleSubmit (у POST /api/medical-records/{id}/attachments нет смысла без recordId). */
  pendingFiles: StagedFile[] = [];
  error: string | null = null;
  loading = true;

  search!: DebouncedSearch<SearchResultItem>;

  // Вложения — с сервера (GET /api/medical-records/{id}/attachments), не из памяти сессии:
  // раньше список не переживал перезагрузку страницы (см. TECH_DEBT).
  attachmentsByRecord: Record<string, Attachment[]> = {};

  // Лимиты загрузки (env-настраиваемые, AttachmentUploadOptions) — грузятся один раз, используются
  // и для клиентской предвалидации, и для подписи «до 8 файлов, 5 МБ каждый» в форме.
  attachmentLimits: AttachmentLimits | null = null;
  uploading = false;

  // Распознавание (кнопка «Распознать» на вложении, задачи 5.2/5.3) — результат живёт на уровне
  // ЗАПИСИ (не вложения): повторное распознавание любого вложения записи полностью заменяет
  // предыдущие показатели/резюме этой записи (см. MedicalDocumentExtractionProcessor).
  extractionStatusByRecord: Record<string, ExtractionStatusResponse | null> = {};
  indicatorsByRecord: Record<string, IndicatorDto[]> = {};
  summaryByRecord: Record<string, RecordSummaryResponse | null> = {};
  conclusionByRecord: Record<string, VisitConclusion | null> = {};
  /** `${recordId}:${attachmentId}` вложения, для которого сейчас идёт запрос «Распознать» — дизейблит именно эту кнопку. */
  recognizingKey: string | null = null;
  private readonly pollHandles = new Map<string, ReturnType<typeof setInterval>>();

  // L1: семьи, которым владелец глобально расшарил записи (общее для обоих видов — единый шаринг).
  shares: string[] = [];

  // Запись, для которой сейчас открыта шторка «Доступ» (null — шторка закрыта).
  accessRecord: MedicalRecord | null = null;

  // undefined — ещё ни разу не загружали.
  private loadedKind: MedicalRecordKind | undefined = undefined;

  constructor() {
    // Реагирует на смену вида, пока панель смонтирована (сейчас оба вида монтируются на разных
    // страницах, но контракт Panel требует этого независимо — см. medkits-panel.component.ts).
    effect(() => {
      const kind = this.kind();
      if (kind === this.loadedKind) return;
      this.resetForm();
      this.accessRecord = null;
      this.search = this.buildSearch(kind);
      void this.refresh();
    });
  }

  ngOnInit(): void {
    // Первичная загрузка — здесь, а не только в effect(): effect выполняется на следующем цикле
    // change detection и может не успеть отработать до первого рендера шаблона.
    if (this.kind() !== this.loadedKind) {
      this.search = this.buildSearch(this.kind());
      void this.refresh();
    }
    if (!this.attachmentLimits) {
      void this.api.getAttachmentLimits().then((limits) => (this.attachmentLimits = limits));
    }
  }

  /** Опрос статуса распознавания использует setInterval — без явной остановки таймеры
   * пережили бы размонтирование панели (переключение вкладки Health-хаба). */
  ngOnDestroy(): void {
    for (const handle of this.pollHandles.values()) clearInterval(handle);
    this.pollHandles.clear();
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
          label: `${dep.name} (${family.name})`,
        });
      }
      for (const member of family.currentMembers ?? []) {
        if (seenUserIds.has(member.id)) continue;
        seenUserIds.add(member.id);
        options.push({
          key: `user:${member.id}`,
          familyDependentId: null,
          targetUserId: member.id,
          label: `${member.displayName} (${family.name})`,
        });
      }
    }
    return options;
  }

  get selectedPatientKey(): string {
    if (this.form.familyDependentId) return `dep:${this.form.familyDependentId}`;
    if (this.form.targetUserId) return `user:${this.form.targetUserId}`;
    return 'self';
  }

  /** Меняет familyDependentId/targetUserId по выбору и подставляет имя в форму для удобства —
   * поле остаётся редактируемым вручную дальше. */
  set selectedPatientKey(key: string) {
    const option = this.patientOptions.find((o) => o.key === key);
    this.form.familyDependentId = option?.familyDependentId ?? null;
    this.form.targetUserId = option?.targetUserId ?? null;
    if (option && option !== SELF_OPTION) {
      this.form.personName = option.label.replace(/\s*\([^)]*\)$/, '');
    }
  }

  private buildSearch(kind: MedicalRecordKind): DebouncedSearch<SearchResultItem> {
    return new DebouncedSearch<SearchResultItem>(
      (q) => this.api.search(q, SEARCH_TYPE_TOKEN[kind]).then((r) => r.items),
      (err) => (err instanceof ApiError ? err.message : 'Не удалось выполнить поиск.'),
    );
  }

  onSearchQueryChange(value: string): void {
    this.search.query = value;
    this.search.onQueryChange();
  }

  /** Поиск отдаёт только id + score (шифрованные поля не индексируются в БД) — рендерим уже
   * загруженные items, отфильтрованные и упорядоченные по совпавшим id. */
  get displayedItems(): MedicalRecord[] {
    if (this.search.query.trim().length < SEARCH_MIN_QUERY_LENGTH || !this.search.searched) {
      return this.items;
    }
    const scoreById = new Map(this.search.items.map((i) => [i.id, i.score]));
    return this.items
      .filter((r) => scoreById.has(r.id))
      .sort((a, b) => scoreById.get(b.id)! - scoreById.get(a.id)!);
  }

  async refresh(): Promise<void> {
    const kind = this.kind();
    this.loadedKind = kind;
    this.loading = true;
    try {
      const [items, shares] = await Promise.all([
        this.api.getMedicalRecords(LIST_KIND_TOKEN[kind]),
        this.api.getMedicalRecordShares(),
      ]);
      this.items = items;
      this.shares = shares;
      // Открытая шторка должна остаться синхронной с перезагруженным состоянием записи.
      if (this.accessRecord) {
        this.accessRecord = this.items.find((r) => r.id === this.accessRecord!.id) ?? null;
      }

      const pairs = await Promise.all(
        items.map(async (item) => [item.id, await this.api.getRecordAttachments(item.id)] as const),
      );
      this.attachmentsByRecord = Object.fromEntries(pairs);
      this.error = null;

      // Уже распознанные ранее записи — подгружаем результат сразу, без повторного клика
      // «Распознать» (переживает перезагрузку страницы, в отличие от старого TECH_DEBT со вложениями).
      await Promise.all(
        items
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
    if (!this.form.personName.trim() || !this.form.recordDate) return;
    try {
      const created = await this.api.createMedicalRecord({
        kind: this.kind(),
        personName: this.form.personName.trim(),
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
        this.uploading = true;
        try {
          for (const staged of this.pendingFiles) {
            try {
              await this.api.uploadAttachment(created.id, staged.file);
            } catch {
              uploadFailed++;
            }
          }
        } finally {
          this.uploading = false;
        }
      }

      this.resetForm();
      await this.refresh();
      this.error = uploadFailed > 0 ? `Запись сохранена, но ${uploadFailed} файлов не загрузилось — прикрепите их к записи ниже.` : null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось сохранить запись.';
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

  /** Сколько ещё файлов можно приложить к этой записи (для подписи под инпутом и клиентской
   * предвалидации) — null, пока лимиты ещё не загружены (GET /api/attachments/limits в ngOnInit). */
  remainingSlots(recordId: string): number | null {
    if (!this.attachmentLimits) return null;
    return Math.max(0, this.attachmentLimits.maxFilesPerRecord - this.attachmentsFor(recordId).length);
  }

  canAddMoreAttachments(recordId: string): boolean {
    return this.remainingSlots(recordId) !== 0;
  }

  /** До 8 файлов за раз (multiple на инпуте) — загружаются последовательно (сервер принимает один
   * файл за запрос), список вложений и остаток слотов обновляются по мере успеха каждого. */
  async handleUpload(recordId: string, event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = ''; // позволяет выбрать те же файлы повторно
    if (files.length === 0) return;

    const { accepted, skippedByCount, tooLarge } = this.filterAgainstLimits(this.attachmentsFor(recordId).length, files);

    this.uploading = true;
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
      this.uploading = false;
    }

    const limits = this.attachmentLimits;
    const problems: string[] = [];
    if (skippedByCount > 0 && limits) problems.push(`не прикреплено ${skippedByCount} файлов сверх лимита (${limits.maxFilesPerRecord} на запись)`);
    if (tooLarge.length > 0 && limits) problems.push(`${tooLarge.length} файлов превышают ${formatMb(limits.maxFileSizeBytes)} и не отправлены`);
    if (failed > 0) problems.push(`${failed} файлов не загрузились`);
    this.error = problems.length > 0 ? `Загрузка завершена частично: ${problems.join(', ')}.` : null;
  }

  // --- Распознавание (кнопка «Распознать», задачи 5.2/5.3) ---

  recognizeKeyFor(recordId: string, attachmentId: string): string {
    return `${recordId}:${attachmentId}`;
  }

  async handleRecognize(record: MedicalRecord, attachmentId: string): Promise<void> {
    const key = this.recognizeKeyFor(record.id, attachmentId);
    this.recognizingKey = key;
    try {
      await this.api.requestExtraction(record.id, attachmentId);
      this.extractionStatusByRecord = { ...this.extractionStatusByRecord, [record.id]: null };
      this.startPolling(record);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось запустить распознавание.';
      this.recognizingKey = null;
    }
  }

  private startPolling(record: MedicalRecord): void {
    const existing = this.pollHandles.get(record.id);
    if (existing) clearInterval(existing);

    const tick = async () => {
      try {
        const status = await this.api.getExtractionStatus(record.id);
        this.extractionStatusByRecord = { ...this.extractionStatusByRecord, [record.id]: status };
        if (EXTRACTION_TERMINAL_STATUSES.includes(status.status)) {
          this.stopPolling(record.id);
          this.recognizingKey = null;
          if (status.status === ExtractionJobStatus.Completed) {
            await this.loadExtractionResult(record);
          } else if (status.error) {
            this.error = status.error;
          }
        }
      } catch (err) {
        this.stopPolling(record.id);
        this.recognizingKey = null;
        this.error = err instanceof ApiError ? err.message : 'Не удалось получить статус распознавания.';
      }
    };

    void tick();
    this.pollHandles.set(record.id, setInterval(() => void tick(), EXTRACTION_POLL_INTERVAL_MS));
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
      personName: '',
      recordDate: '',
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
