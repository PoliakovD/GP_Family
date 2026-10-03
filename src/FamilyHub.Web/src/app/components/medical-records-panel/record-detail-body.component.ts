import { Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MedicalRecordKind } from '../../models/types';
import type {
  ExtractionStatusResponse, IndicatorDto, MedicalRecord, RecordSummaryResponse, VisitConclusion,
} from '../../models/types';
import { AttachmentListComponent } from '../../shared/attachment-list/attachment-list.component';
import type { PipelineStep } from '../../shared/pipeline-progress/pipeline-progress.component';
import { IndicatorTableComponent } from './indicator-table.component';
import { RecordRecognitionStatusComponent } from './record-recognition-status.component';

/**
 * Тело экрана одной записи (вынесено из MedicalRecordsPanelComponent): распознавание, описание,
 * баннеры биоматериала, файлы, резюме от ИИ, показатели (анализ) или заключение врача (приём).
 * Данными и опросами владеет панель; компонент только показывает и сообщает о действиях.
 */
@Component({
  selector: 'app-record-detail-body',
  standalone: true,
  imports: [AttachmentListComponent, IndicatorTableComponent, RecordRecognitionStatusComponent, RouterLink],
  templateUrl: './record-detail-body.component.html',
  styles: `
    .ai-summary-note {
      display: flex;
      gap: 6px;
      align-items: flex-start;
      font-size: 0.8235rem;
      color: var(--color-neutral-800);
    }
  `,
})
export class RecordDetailBodyComponent {
  readonly record = input.required<MedicalRecord>();
  // распознавание
  readonly steps = input<PipelineStep[] | undefined>(undefined);
  readonly recognizing = input(false);
  readonly status = input<ExtractionStatusResponse | null | undefined>(null);
  /** Владелец записи (баннер «уточните биоматериал» — только ему). */
  readonly canEdit = input(false);
  // секции, которые разворачиваются кнопками шапки
  readonly filesOpen = input(false);
  readonly summaryOpen = input(false);
  readonly hasSummary = input(false);
  readonly summary = input<RecordSummaryResponse | null | undefined>(null);
  readonly summaryRegenerating = input(false);
  readonly aiUnavailable = input(false);
  // показатели / заключение
  readonly indicators = input<readonly IndicatorDto[]>([]);
  readonly conclusion = input<VisitConclusion | null | undefined>(null);
  readonly wide = input(false);

  readonly recognize = output<void>();
  /** «Уточнить» в баннере биоматериала — подсказка модели для формы правки. */
  readonly clarifySpecimen = output<string>();
  readonly filesChanged = output<void>();
  readonly openIndicator = output<IndicatorDto>();
  readonly indicatorCreated = output<void>();
  readonly openKb = output<string>();

  protected readonly Kind = MedicalRecordKind;
}
