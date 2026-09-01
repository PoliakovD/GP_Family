import { Component, ElementRef, OnDestroy, OnInit, ViewChild, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NgTemplateOutlet } from '@angular/common';
import { Router } from '@angular/router';
import { ApiService, ApiError } from '../../services/api.service';
import { AuthService } from '../../services/auth.service';
import { FamilyStateService } from '../../services/family-state.service';
import { BreakpointService } from '../../services/breakpoint.service';
import { ToastService } from '../../shared/toast/toast.service';
import type { AttachmentLimits, MedicalRecordKind } from '../../models/types';
import { buildPatientOptions, type PatientOption } from '../../shared/util/patient-options';
import { ACCEPTED_ATTACHMENT_TYPES, filterFilesAgainstLimits, formatMb } from '../../shared/util/attachment-upload';
import { MEDICAL_RECORD_KIND_LABELS, medicalRecordKindBasePath, type MedicalRecordKindLabels } from '../../shared/util/medical-record-labels';
import { ExpandableComponent } from '../../shared/expandable/expandable.component';

/** Файл, ожидающий загрузки — ещё не отправленный (запись создастся только по «Сохранить»).
 * previewUrl — только для картинок (см. medications-panel.photos). */
interface StagedFile {
  file: File;
  previewUrl: string | null;
}

/** Сегодняшняя дата в формате input[type=date] — дефолт формы, распознавание может позже
 * переопределить её датой, найденной в самом документе. */
function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}

let nextInstanceId = 0;

/**
 * Экран добавления записи (редизайн v3, PR7) — реальный дочерний роут (`/health/records/new`,
 * `/health/visits/new`), не инлайн-форма над списком (была — `createOpen` в
 * medical-records-panel.component.ts). Сам решает представление по BreakpointService: `isWide` —
 * оверлей с боковой панелью (тот же приём, что `indicator-info-panel`), узкие экраны —
 * полноэкранно. Патиент-опции/лимиты вложений/подписи по виду записи — общая логика со списком,
 * см. shared/util/patient-options.ts, attachment-upload.ts, medical-record-labels.ts.
 */
@Component({
  selector: 'app-record-add',
  standalone: true,
  imports: [FormsModule, NgTemplateOutlet, ExpandableComponent],
  templateUrl: './record-add.component.html',
  styleUrl: './record-add.component.scss',
})
export class RecordAddComponent implements OnInit, OnDestroy {
  readonly kind = input.required<MedicalRecordKind>();

  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly state = inject(FamilyStateService);
  private readonly router = inject(Router);
  private readonly breakpoints = inject(BreakpointService);
  private readonly toast = inject(ToastService);

  readonly acceptedFileTypes = ACCEPTED_ATTACHMENT_TYPES;
  readonly fileInputId = `record-add-file-input-${nextInstanceId}`;
  readonly doctorsDatalistId = `record-add-doctors-datalist-${nextInstanceId++}`;

  saving = false;
  error: string | null = null;

  form = {
    recordDate: todayIso(),
    doctor: '',
    description: '',
    familyDependentId: null as string | null,
    targetUserId: null as string | null,
  };

  /** Тумблер «Распознать бланк» (редизайн v3, PR7) — по умолчанию включён, запускает
   * распознавание автоматически сразу после сохранения (не отдельная ручная кнопка). */
  autoRecognize = true;

  pendingFiles: StagedFile[] = [];
  attachmentLimits: AttachmentLimits | null = null;
  doctorSuggestions: string[] = [];

  get isWide(): boolean {
    return this.breakpoints.tier() === 'wide';
  }

  get labels(): MedicalRecordKindLabels {
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

  ngOnInit(): void {
    void this.api.getAttachmentLimits().then((limits) => (this.attachmentLimits = limits));
    void this.api.getDoctorSuggestions().then((doctors) => (this.doctorSuggestions = doctors));
  }

  ngOnDestroy(): void {
    this.clearPendingFiles();
  }

  private kindBasePath(): string {
    return medicalRecordKindBasePath(this.kind());
  }

  cancel(): void {
    void this.router.navigate([this.kindBasePath()]);
  }

  canAddMorePendingFiles(): boolean {
    return !this.attachmentLimits || this.pendingFiles.length < this.attachmentLimits.maxFilesPerRecord;
  }

  onPendingFilesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';
    if (files.length === 0) return;

    const { accepted, skippedByCount, tooLarge } = filterFilesAgainstLimits(this.attachmentLimits, this.pendingFiles.length, files);
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

      let uploadFailed = 0;
      for (const staged of this.pendingFiles) {
        try {
          await this.api.uploadAttachment(created.id, staged.file);
        } catch {
          uploadFailed++;
        }
      }
      if (uploadFailed > 0) {
        this.toast.error(`Запись сохранена, но ${uploadFailed} файлов не загрузилось — прикрепите их к записи ниже.`);
      }

      // Редизайн v3 (PR7) — автораспознавание при сохранении: fire-and-forget, не дожидаемся
      // завершения — живой прогресс дальше показывает уже существующий pipelineStepsByRecord на
      // карточке списка/экране записи (та же UI, что и у ручной кнопки «Распознать»).
      const uploadedCount = this.pendingFiles.length - uploadFailed;
      if (this.autoRecognize && uploadedCount > 0) {
        void this.api.requestExtraction(created.id).catch(() => {});
      }

      this.clearPendingFiles();

      if (this.isWide) {
        // Десктоп: мокап не показывает отдельный шаг проверки — панель закрывается, живой
        // прогресс распознавания виден прямо в списке (см. PR6/inline-раскрытие карточки).
        void this.router.navigate([this.kindBasePath()]);
      } else {
        // Мобайл: второй шаг «Проверьте распознанное» — это существующий экран открытой записи
        // (PR6), с одноразовым баннером-подсказкой по firstReview.
        void this.router.navigate([this.kindBasePath(), created.id], { queryParams: { firstReview: '1' } });
      }
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось сохранить запись.';
    } finally {
      this.saving = false;
    }
  }
}
