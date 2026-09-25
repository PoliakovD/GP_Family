import { Component, OnDestroy, OnInit, inject, input, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { Router } from '@angular/router';
import { ApiService, ApiError } from '../../services/api.service';
import { AuthService } from '../../services/auth.service';
import { FamilyStateService } from '../../services/family-state.service';
import { BreakpointService } from '../../services/breakpoint.service';
import type { AttachmentLimits, ExtractionLimits, MedicalRecordKind } from '../../models/types';
import { buildPatientOptions, type PatientOption } from '../../shared/util/patient-options';
import { ACCEPTED_ATTACHMENT_TYPES, formatMb } from '../../shared/util/attachment-upload';
import { MEDICAL_RECORD_KIND_LABELS, medicalRecordKindBasePath } from '../../shared/util/medical-record-labels';
import { FileViewerComponent } from '../../shared/file-viewer/file-viewer.component';
import { PersonChipComponent } from '../../shared/person-chip/person-chip.component';

type BatchItemStatus = 'pending' | 'creating' | 'uploading' | 'queued' | 'failed' | 'skipped';

interface BatchItem {
  file: File;
  previewUrl: string | null;
  status: BatchItemStatus;
  recordId?: string;
  error?: string;
}

function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}

let nextInstanceId = 0;

/**
 * Батч-загрузка (кнопка «+ Несколько» рядом с «+ Добавить», см. medical-records-panel) — за одну
 * операцию загружает N документов ОДНОГО пациента так, что каждый документ становится СВОЕЙ
 * записью со своим прогоном пайплайна (в отличие от record-add.component.ts, где N файлов — это
 * страницы ОДНОГО бланка/приёма, мержащиеся в один набор показателей). Вид записи (Анализ/Врач)
 * не спрашивается — модель определяет его по каждому документу (см. DocumentKindClassifier,
 * MedicalRecord.KindIsAutoDetected); provisional kind (аргумент роута) — только вкладка, куда
 * записи попадают ДО того, как пайплайн определит вид, и откуда их видно в бэйдже.
 *
 * Существующий флоу («+ Добавить») этот компонент не заменяет и не трогает — он остаётся для
 * случая "несколько страниц одного документа".
 */
@Component({
  selector: 'app-record-batch-add',
  standalone: true,
  imports: [NgTemplateOutlet, FileViewerComponent, PersonChipComponent],
  templateUrl: './record-batch-add.component.html',
  styleUrl: './record-batch-add.component.scss',
})
export class RecordBatchAddComponent implements OnInit, OnDestroy {
  readonly kind = input.required<MedicalRecordKind>();

  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly state = inject(FamilyStateService);
  private readonly router = inject(Router);
  private readonly breakpoints = inject(BreakpointService);

  readonly acceptedFileTypes = ACCEPTED_ATTACHMENT_TYPES;
  readonly fileInputId = `record-batch-add-file-input-${nextInstanceId++}`;

  form = {
    familyDependentId: null as string | null,
    targetUserId: null as string | null,
  };

  items = signal<BatchItem[]>([]);
  running = signal(false);
  done = signal(false);
  stoppedByLimit: string | null = null;
  /** Хотя бы один документ поставлен в очередь при недоступном ИИ (ответ waiting_for_ai) — говорим,
   * что ничего не потеряно и распознавание стартует само. */
  waitingForAi = false;
  attachmentLimits: AttachmentLimits | null = null;
  extractionLimits: ExtractionLimits | null = null;

  get isWide(): boolean {
    return this.breakpoints.tier() === 'wide';
  }

  get labels() {
    return MEDICAL_RECORD_KIND_LABELS[this.kind()];
  }

  get patientOptions(): PatientOption[] {
    return buildPatientOptions(this.state.activeFamilies(), this.auth.me()?.userId);
  }

  get selectedPatientKey(): string {
    if (this.form.familyDependentId) return `dep:${this.form.familyDependentId}`;
    if (this.form.targetUserId) return `user:${this.form.targetUserId}`;
    return 'self';
  }

  set selectedPatientKey(key: string) {
    const option = this.patientOptions.find((o) => o.key === key);
    this.form.familyDependentId = option?.familyDependentId ?? null;
    this.form.targetUserId = option?.targetUserId ?? null;
  }

  /** Сколько документов ещё можно застейджить — минимум из двух независимых лимитов
   * (ExtractionLimitsOptions.MaxBatchDocuments и дневная квота распознаваний, см.
   * GetLimitsAsync на бэкенде) — оба мягкие, настоящий барьер всё равно на сервере. */
  get remainingSlots(): number {
    const byBatch = this.extractionLimits ? this.extractionLimits.maxBatchDocuments - this.items().length : Infinity;
    const byDailyQuota = this.extractionLimits
      ? this.extractionLimits.dailyQuota - this.extractionLimits.usedToday - this.items().length
      : Infinity;
    return Math.max(0, Math.min(byBatch, byDailyQuota));
  }

  ngOnInit(): void {
    void this.api.getAttachmentLimits().then((limits) => (this.attachmentLimits = limits));
    void this.api.getExtractionLimits().then((limits) => (this.extractionLimits = limits));
  }

  ngOnDestroy(): void {
    this.clearItems();
  }

  private kindBasePath(): string {
    return medicalRecordKindBasePath(this.kind());
  }

  cancel(): void {
    void this.router.navigate([this.kindBasePath()]);
  }

  onFilesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';
    if (files.length === 0) return;

    const room = this.remainingSlots;
    const accepted = files.slice(0, room);
    const tooLarge = this.attachmentLimits
      ? accepted.filter((f) => f.size > this.attachmentLimits!.maxFileSizeBytes)
      : [];
    const withinSize = this.attachmentLimits ? accepted.filter((f) => f.size <= this.attachmentLimits!.maxFileSizeBytes) : accepted;

    const added: BatchItem[] = withinSize.map((file) => ({
      file,
      previewUrl: file.type.startsWith('image/') ? URL.createObjectURL(file) : null,
      status: 'pending',
    }));
    this.items.set([...this.items(), ...added]);

    const problems: string[] = [];
    if (files.length > room) problems.push(`не добавлено ${files.length - room} файлов сверх лимита`);
    if (tooLarge.length > 0 && this.attachmentLimits) {
      problems.push(`${tooLarge.length} файлов превышают ${formatMb(this.attachmentLimits.maxFileSizeBytes)} и не добавлены`);
    }
    this.stoppedByLimit = problems.length > 0 ? `${problems.join(', ')}.` : null;
  }

  removeItem(index: number): void {
    const [removed] = this.items().slice(index, index + 1);
    if (removed?.previewUrl) URL.revokeObjectURL(removed.previewUrl);
    this.items.set(this.items().filter((_, i) => i !== index));
    if (this.viewerIndex === index) this.closePreview();
  }

  viewerIndex: number | null = null;

  get viewerFile(): File | null {
    return this.viewerIndex !== null ? (this.items()[this.viewerIndex]?.file ?? null) : null;
  }

  openPreview(index: number): void {
    this.viewerIndex = index;
  }

  closePreview(): void {
    this.viewerIndex = null;
  }

  private clearItems(): void {
    this.items().forEach((i) => i.previewUrl && URL.revokeObjectURL(i.previewUrl));
    this.items.set([]);
  }

  private setItemStatus(index: number, patch: Partial<BatchItem>): void {
    this.items.set(this.items().map((it, i) => (i === index ? { ...it, ...patch } : it)));
  }

  /** 429 — describe() по коду ошибки (см. patterns/frontend_web.md, TelegramBindComponent) —
   * отдельная ветка на превышение лимита/квоты пайплайна, не общий fallback-текст. */
  private describeLimitError(err: unknown): string | null {
    if (!(err instanceof ApiError) || err.status !== 429) return null;
    return err.message || 'Слишком много запросов — попробуйте позже.';
  }

  async submit(): Promise<void> {
    if (this.running() || this.items().length === 0) return;
    this.running.set(true);
    this.done.set(false);
    this.stoppedByLimit = null;
    this.waitingForAi = false;

    const recordDate = todayIso();
    const items = this.items();

    for (let i = 0; i < items.length; i++) {
      if (items[i].status !== 'pending') continue;

      this.setItemStatus(i, { status: 'creating' });
      try {
        const created = await this.api.createMedicalRecord({
          kind: this.kind(),
          recordDate,
          doctor: null,
          description: null,
          hideFromFamilyIds: null,
          familyDependentId: this.form.familyDependentId,
          targetUserId: this.form.targetUserId,
          autoDetectKind: true,
        });
        this.setItemStatus(i, { status: 'uploading', recordId: created.id });

        await this.api.uploadAttachment(created.id, items[i].file);
        const extraction = await this.api.requestExtraction(created.id);
        if (extraction?.code === 'waiting_for_ai') this.waitingForAi = true;
        this.setItemStatus(i, { status: 'queued' });
      } catch (err) {
        const limitMessage = this.describeLimitError(err);
        if (limitMessage) {
          // Лимит/квота — останавливаем весь батч, остаток помечаем skipped (не failed — это не
          // ошибка ИМЕННО этого файла, ему просто не хватило места в пределах лимита).
          this.setItemStatus(i, { status: 'skipped', error: limitMessage });
          this.stoppedByLimit = limitMessage;
          for (let j = i + 1; j < items.length; j++) {
            this.setItemStatus(j, { status: 'skipped' });
          }
          break;
        }
        this.setItemStatus(i, {
          status: 'failed',
          error: err instanceof ApiError ? err.message : 'Не удалось загрузить документ.',
        });
      }
    }

    this.running.set(false);
    this.done.set(true);
  }

  get succeededCount(): number {
    return this.items().filter((i) => i.status === 'queued').length;
  }

  get failedCount(): number {
    return this.items().filter((i) => i.status === 'failed' || i.status === 'skipped').length;
  }

  finish(): void {
    void this.router.navigate([this.kindBasePath()]);
  }
}
