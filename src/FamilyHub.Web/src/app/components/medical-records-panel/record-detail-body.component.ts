import { Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Hourglass, Sparkles } from 'lucide-angular';
import { MedicalRecordKind } from '../../models/types';
import type {
  ExtractionStatusResponse, IndicatorDto, MedicalRecord, RecordSummaryResponse, VisitConclusion,
} from '../../models/types';
import { AttachmentListComponent } from '../../shared/attachment-list/attachment-list.component';
import type { PipelineStep } from '../../shared/pipeline-progress/pipeline-progress.component';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { IndicatorTableComponent } from './indicator-table.component';
import type { IndicatorStatusFilter } from './indicator-display';
import { RecordRecognitionStatusComponent } from './record-recognition-status.component';

/**
 * Тело экрана одной записи (вынесено из MedicalRecordsPanelComponent): распознавание, описание,
 * баннеры биоматериала, файлы, резюме от ИИ, показатели (анализ) или заключение врача (приём).
 * Данными и опросами владеет панель; компонент только показывает и сообщает о действиях.
 */
@Component({
  selector: 'app-record-detail-body',
  standalone: true,
  imports: [AttachmentListComponent, IconComponent, IndicatorTableComponent, RecordRecognitionStatusComponent, RouterLink],
  templateUrl: './record-detail-body.component.html',
  styles: `
    .rdb { display: flex; flex-direction: column; gap: var(--space-4); min-width: 0; }
    .rdb-text { margin: 0; font: var(--text-body); color: var(--color-text); }
    .rdb-note { margin: 0; font: var(--text-caption); color: var(--color-text-muted); }
    .rdb-subtitle { margin: var(--space-2) 0 0; font: var(--text-body); font-weight: 600; }
    .rdb-list, .rdb-meds { margin: 0; padding-left: var(--space-5); font: var(--text-body-sm); display: flex; flex-direction: column; gap: var(--space-1); }
    .rdb-meds .btn-link { margin-left: var(--space-2); }
    .rdb-summary-body { display: flex; flex-direction: column; gap: var(--space-3); }
    .rdb-stale > :not(.rdb-note:first-child) { opacity: 0.55; }
    .ai-summary-note {
      display: flex;
      gap: var(--space-2);
      align-items: flex-start;
      margin: 0;
      padding: var(--space-3) var(--space-4);
      border-radius: var(--radius-md);
      background: var(--color-surface-sunken);
      font: var(--text-caption);
      color: var(--color-text-secondary);
      app-icon { flex: none; color: var(--color-accent); }
    }
    .alert-info { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; }
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

  /** Нажатый счётчик-фильтр шапки — таблица показывает только эту группу. */
  readonly statusFilter = input<IndicatorStatusFilter | null>(null);

  protected readonly Kind = MedicalRecordKind;
  protected readonly icons = { Hourglass, Sparkles };
}
