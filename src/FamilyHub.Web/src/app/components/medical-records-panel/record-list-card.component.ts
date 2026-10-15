import { Component, computed, input, output } from '@angular/core';
import { ChevronRight, FlaskConical, Stethoscope } from 'lucide-angular';
import { MedicalRecordKind } from '../../models/types';
import type { ExtractionStatusResponse, MedicalRecord } from '../../models/types';
import { ActionMenuComponent, type ActionMenuItem } from '../../shared/action-menu/action-menu.component';
import type { PipelineStep } from '../../shared/pipeline-progress/pipeline-progress.component';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { IconTileComponent } from '../../shared/ui/icon-tile/icon-tile.component';
import { formatDayMonthYear } from '../../shared/util/date-format';
import { attachmentCountLabel, indicatorCountLabel, recordShortName } from './record-display';
import { RecordRecognitionStatusComponent } from './record-recognition-status.component';

/**
 * Строка записи в списке (DESIGN §3 «Строки», как nav-row): плитка вида → название 16/600 + подпись
 * (дата · показатели · файлы · врач) → бейдж статуса → «⋯» (десктоп) → шеврон. Вся строка открывает
 * запись; под строкой — блок распознавания. Разделители строк рисует list-card родителя.
 */
@Component({
  selector: 'app-record-list-card',
  standalone: true,
  imports: [ActionMenuComponent, IconComponent, IconTileComponent, RecordRecognitionStatusComponent],
  template: `
    <div class="rl-row">
      <button type="button" class="rl-main" (click)="open.emit()">
        <app-icon-tile [icon]="kindIcon()" tone="accent" size="sm" />
        <span class="rl-text">
          <span class="rl-title">{{ shortName() }}</span>
          <span class="rl-sub">{{ subtitle() }}</span>
        </span>
        @if (record().kindIsAutoDetected) {
          <!-- Пакетная загрузка — вид ещё не определён пайплайном; снимается после распознавания. -->
          <span class="badge badge-neutral" title="Вид записи будет уточнён после распознавания">вид уточняется</span>
        } @else if (record().abnormalIndicatorCount > 0) {
          <span class="badge badge-danger">{{ record().abnormalIndicatorCount }} вне нормы</span>
        } @else if (record().normalIndicatorCount > 0) {
          <span class="badge badge-success">в норме</span>
        }
        @if (!wide()) {
          <app-icon class="rl-chevron" [icon]="icons.ChevronRight" [size]="18" />
        }
      </button>
      @if (wide()) {
        <app-action-menu [actions]="actions()" label="Действия с записью" />
      }
    </div>

    <div class="rl-recognition">
      <app-record-recognition-status
        [record]="record()"
        [steps]="steps()"
        [recognizing]="recognizing()"
        [status]="status()"
        (recognize)="recognize.emit()"
      />
    </div>
  `,
  styles: `
    :host { display: block; position: relative; }

    // Разделитель с отступами 24 по бокам (DESIGN §3 «list-card»).
    :host(:not(:first-of-type))::before {
      content: '';
      position: absolute;
      top: 0;
      left: var(--space-6);
      right: var(--space-6);
      border-top: 1px solid var(--color-divider);
    }

    .rl-row {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      padding-right: var(--space-4);
    }

    .rl-main {
      display: flex;
      align-items: center;
      gap: var(--space-3);
      flex: 1;
      min-width: 0;
      min-height: 64px;
      padding: var(--space-3) var(--space-2) var(--space-3) var(--space-6);
      border: 0;
      background: none;
      font: inherit;
      color: var(--color-text);
      text-align: left;
      cursor: pointer;
      border-radius: var(--radius-md);
    }

    @media (hover: hover) { .rl-row:hover { background: var(--color-surface-muted); } }
    .rl-main:focus-visible { outline-offset: -2px; }

    .rl-text { display: flex; flex-direction: column; gap: 2px; flex: 1; min-width: 0; }
    .rl-title { font: var(--text-body); font-weight: 600; overflow-wrap: anywhere; }
    .rl-sub { font: var(--text-caption); color: var(--color-text-muted); }
    .rl-chevron { flex: none; color: var(--color-text-muted); }

    // Распознавание («Распознать», прогресс, плашки) — под строкой, с отступом текста.
    .rl-recognition {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: 0 var(--space-6) 0 calc(var(--space-6) + 40px + var(--space-3));

      &:has(> app-record-recognition-status > *) { padding-bottom: var(--space-3); }
    }

    @media (max-width: 1023px) {
      :host(:not(:first-of-type))::before { left: var(--space-4); right: var(--space-4); }
      .rl-row { padding-right: var(--space-3); }
      .rl-main { padding-left: var(--space-4); align-items: flex-start; flex-wrap: wrap; }
      .rl-text { flex-basis: calc(100% - 40px - var(--space-3) - 18px - var(--space-3) * 2); }
      .rl-main .badge { margin-left: calc(40px + var(--space-3)); order: 3; }
      .rl-chevron { align-self: center; }
      .rl-recognition { padding: 0 var(--space-4); }
    }
  `,
})
export class RecordListCardComponent {
  readonly record = input.required<MedicalRecord>();
  readonly wide = input(false);
  readonly actions = input<ActionMenuItem[]>([]);
  readonly steps = input<PipelineStep[] | undefined>(undefined);
  readonly recognizing = input(false);
  readonly status = input<ExtractionStatusResponse | null | undefined>(null);

  readonly open = output<void>();
  readonly recognize = output<void>();

  protected readonly icons = { ChevronRight };

  protected readonly kindIcon = computed(() =>
    this.record().kind === MedicalRecordKind.DoctorVisit ? Stethoscope : FlaskConical);

  protected readonly shortName = computed(() => recordShortName(this.record()));

  /** «3 октября 2026 · 20 показателей · 1 файл · Врач: Иванова М. П.» — только то, что есть. */
  protected readonly subtitle = computed(() => {
    const r = this.record();
    const parts = [formatDayMonthYear(r.recordDate)];
    if (r.indicatorCount > 0) parts.push(indicatorCountLabel(r));
    if (r.attachmentCount > 0) parts.push(attachmentCountLabel(r));
    if (r.doctor) parts.push(`Врач: ${r.doctor}`);
    return parts.join(' · ');
  });
}
