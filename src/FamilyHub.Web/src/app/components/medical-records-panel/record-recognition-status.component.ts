import { Component, input, output } from '@angular/core';
import { ExtractionJobStatus, ExtractionStatus } from '../../models/types';
import type { ExtractionStatusResponse, MedicalRecord } from '../../models/types';
import { PipelineProgressComponent, type PipelineStep } from '../../shared/pipeline-progress/pipeline-progress.component';

/**
 * Распознавание записи — общий блок для карточки в списке и экрана записи (вынесено из
 * MedicalRecordsPanelComponent): кнопка «Распознать», живой прогресс, «распознано не всё» и
 * постоянная плашка о прошлом сбое. Опросом статуса владеет панель (ExtractionPoller).
 */
@Component({
  selector: 'app-record-recognition-status',
  standalone: true,
  imports: [PipelineProgressComponent],
  template: `
    @if (record().unrecognizedAttachmentCount > 0) {
      <button
        class="btn btn-primary btn-sm align-self-start mt-1"
        [disabled]="recognizing()"
        (click)="recognize.emit()"
      >
        @if (recognizing()) {
          <span class="recognize-spinner" aria-hidden="true"></span>
        } @else {
          <i class="ph ph-magic-wand" aria-hidden="true"></i>
        }
        Распознать
      </button>
    }

    @if (steps(); as steps) {
      <app-pipeline-progress [steps]="steps" />
      @if (recognizing()) {
        <p class="muted mb-1">
          Обычно это занимает 1–3 минуты. Страницу можно закрыть — распознавание продолжится, а
          показатели появятся в этой записи.
        </p>
      }
    }

    @if (status()?.status === ExtractionJobStatus.Completed && status()?.error) {
      <div class="alert-info mt-1" role="status">
        <i class="ph ph-warning" aria-hidden="true"></i>
        Распознано не всё. Сверьте показатели с бланком и добавьте недостающие вручную.
      </div>
    }

    <!-- Живой прогресс показывает сбой только пока виджет на экране; после ухода со страницы эта
         постоянная плашка по record.extractionStatus — единственный способ узнать о сбое, не
         нажимая «Распознать» вслепую. -->
    @if (record().extractionStatus === ExtractionStatus.Failed && !steps()) {
      <div class="alert-danger" style="margin-top:4px;">
        Не удалось распознать документ в прошлый раз.
        @if (record().unrecognizedAttachmentCount > 0) { Нажмите «Распознать» ещё раз. }
      </div>
    }
  `,
  styles: `
    :host { display: contents; }

    .recognize-spinner {
      display: inline-block;
      width: 12px;
      height: 12px;
      border-radius: 50%;
      border: 2px solid var(--color-divider);
      border-top-color: var(--color-accent);
      animation: recognize-spinner-rotate 0.7s linear infinite;
    }

    @keyframes recognize-spinner-rotate {
      to { transform: rotate(360deg); }
    }
  `,
})
export class RecordRecognitionStatusComponent {
  readonly record = input.required<MedicalRecord>();
  readonly steps = input<PipelineStep[] | undefined>(undefined);
  readonly recognizing = input(false);
  readonly status = input<ExtractionStatusResponse | null | undefined>(null);
  readonly recognize = output<void>();

  protected readonly ExtractionJobStatus = ExtractionJobStatus;
  protected readonly ExtractionStatus = ExtractionStatus;
}
