import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges, inject } from '@angular/core';
import { ApiService, ApiError } from '../../services/api.service';
import { AttachmentPreviewStatus, type Attachment, type AttachmentLimits } from '../../models/types';
import { ACCEPTED_ATTACHMENT_TYPES, filterFilesAgainstLimits, formatMb } from '../util/attachment-upload';
import { describeFile } from '../util/file-type';
import { LoadingSpinnerComponent } from '../loading-spinner/loading-spinner.component';
import { FileViewerComponent } from '../file-viewer/file-viewer.component';

/**
 * Секция «Файлы» прямо в раскрытой карточке записи (редизайн — раньше список вложений жил в
 * отдельной модалке «Файлы» из меню «…», недоступной на узком экране в списке вовсе). Владеет
 * своим состоянием (список/лимиты/загрузка) — переиспользуется и списком (medical-records-panel),
 * и мобильным экраном открытой записи без дублирования fetch-логики.
 *
 * Миниатюры — только для уже готовых превью (PreviewStatus=Ready, известно из самого списка
 * вложений без похода на сервер): точечно догружает GET /preview ТОЛЬКО за thumbnailUrl этих
 * файлов, не для Pending/Failed/Unsupported — им нечего показать, кроме иконки типа, а опрашивать
 * Pending в списке незачем (это уже делает открытый app-file-viewer для активного файла).
 */
@Component({
    selector: 'app-attachment-list',
    imports: [LoadingSpinnerComponent, FileViewerComponent],
    templateUrl: './attachment-list.component.html',
    styleUrl: './attachment-list.component.scss'
})
export class AttachmentListComponent implements OnChanges {
  @Input({ required: true }) recordId!: string;
  /** Emitted после успешной/частичной загрузки — счётчики на MedicalRecord (attachmentCount и
   * т.п.) устарели, родитель должен перечитать запись (см. medical-records-panel.refresh()). */
  @Output() changed = new EventEmitter<void>();

  private readonly api = inject(ApiService);

  readonly acceptedFileTypes = ACCEPTED_ATTACHMENT_TYPES;
  readonly PreviewStatus = AttachmentPreviewStatus;

  attachments: Attachment[] = [];
  limits: AttachmentLimits | null = null;
  loading = false;
  uploading = false;
  error: string | null = null;
  thumbnailByAttachment: Record<string, string | null> = {};

  viewerOpen = false;
  viewerIndex = 0;

  private loadedForRecordId: string | null = null;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['recordId'] && this.recordId && this.recordId !== this.loadedForRecordId) {
      void this.load();
    }
  }

  fileInfo(a: Attachment) {
    return describeFile(a.contentType, a.fileName);
  }

  remainingSlots(): number | null {
    if (!this.limits) return null;
    return Math.max(0, this.limits.maxFilesPerRecord - this.attachments.length);
  }

  canAddMore(): boolean {
    return this.remainingSlots() !== 0;
  }

  openViewer(index: number): void {
    this.viewerIndex = index;
    this.viewerOpen = true;
  }

  closeViewer(): void {
    this.viewerOpen = false;
  }

  /** До 8 файлов за раз (multiple на инпуте) — загружаются последовательно (сервер принимает
   * один файл за запрос), список и остаток слотов обновляются по мере успеха каждого. */
  async handleUpload(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = ''; // позволяет выбрать те же файлы повторно
    if (files.length === 0) return;

    const { accepted, skippedByCount, tooLarge } = filterFilesAgainstLimits(this.limits, this.attachments.length, files);

    this.uploading = true;
    let failed = 0;
    let uploadedIndex = -1;
    try {
      for (const file of accepted) {
        try {
          const attachment = await this.api.uploadAttachment(this.recordId, file);
          this.attachments = [...this.attachments, attachment];
          uploadedIndex = this.attachments.length - 1;
        } catch {
          failed++;
        }
      }
    } finally {
      this.uploading = false;
    }

    const limits = this.limits;
    const problems: string[] = [];
    if (skippedByCount > 0 && limits) problems.push(`не прикреплено ${skippedByCount} файлов сверх лимита (${limits.maxFilesPerRecord} на запись)`);
    if (tooLarge.length > 0 && limits) problems.push(`${tooLarge.length} файлов превышают ${formatMb(limits.maxFileSizeBytes)} и не отправлены`);
    if (failed > 0) problems.push(`${failed} файлов не загрузились`);
    this.error = problems.length > 0 ? `Загрузка завершена частично: ${problems.join(', ')}.` : null;

    this.changed.emit();

    // «Сразу после загрузки файла» (см. план) — открываем вьюер на последнем успешно загруженном
    // файле; превью там ещё Pending, вьюер сам покажет спиннер и дождётся готовности опросом.
    if (uploadedIndex >= 0) this.openViewer(uploadedIndex);
  }

  private async load(): Promise<void> {
    this.loadedForRecordId = this.recordId;
    this.loading = true;
    this.error = null;
    try {
      const [attachments, limits] = await Promise.all([
        this.api.getRecordAttachments(this.recordId),
        this.limits ? Promise.resolve(this.limits) : this.api.getAttachmentLimits(),
      ]);
      this.attachments = attachments;
      this.limits = limits;
      void this.loadThumbnails();
    } catch (err) {
      this.loadedForRecordId = null; // разрешает повторную попытку при следующем раскрытии карточки
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить список файлов.';
    } finally {
      this.loading = false;
    }
  }

  private async loadThumbnails(): Promise<void> {
    const ready = this.attachments.filter((a) => a.previewStatus === AttachmentPreviewStatus.Ready);
    await Promise.all(
      ready.map(async (a) => {
        try {
          const preview = await this.api.getAttachmentPreview(a.id);
          this.thumbnailByAttachment = { ...this.thumbnailByAttachment, [a.id]: preview.thumbnailUrl };
        } catch {
          // Тихо — просто останется дефолтная иконка типа файла вместо миниатюры.
        }
      }),
    );
  }
}
