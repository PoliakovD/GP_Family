import { Component, EventEmitter, Input, Output } from '@angular/core';
import { describeFile, formatFileSize } from '../../util/file-type';
import { LoadingSpinnerComponent } from '../../loading-spinner/loading-spinner.component';

/** Карточка «Скачать» — общее состояние для Pending (спиннер вместо кнопки), Failed/Unsupported
 * и любого формата, для которого сервер не строит превью (см. AttachmentPreviewRenderer). Ни
 * ошибок, ни пустых экранов — только понятная карточка с типом/именем/размером (см. план). */
@Component({
    selector: 'app-unsupported-preview',
    imports: [LoadingSpinnerComponent],
    templateUrl: './unsupported-preview.component.html',
    styleUrl: './unsupported-preview.component.scss'
})
export class UnsupportedPreviewComponent {
  @Input({ required: true }) fileName!: string;
  @Input({ required: true }) contentType!: string;
  @Input({ required: true }) sizeBytes!: number;
  @Input() pending = false;
  @Input() failureReason: string | null = null;
  @Input() canDownload = false;
  @Output() download = new EventEmitter<void>();

  get info() {
    return describeFile(this.contentType, this.fileName);
  }

  get sizeLabel(): string {
    return formatFileSize(this.sizeBytes);
  }
}
