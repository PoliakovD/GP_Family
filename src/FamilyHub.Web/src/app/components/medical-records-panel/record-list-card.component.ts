import { Component, input, output } from '@angular/core';
import type { ExtractionStatusResponse, MedicalRecord } from '../../models/types';
import { ActionMenuComponent, type ActionMenuItem } from '../../shared/action-menu/action-menu.component';
import type { PipelineStep } from '../../shared/pipeline-progress/pipeline-progress.component';
import { attachmentCountLabel, indicatorCountLabel, recordShortName } from './record-display';
import { RecordRecognitionStatusComponent } from './record-recognition-status.component';

/**
 * Карточка записи в списке (вынесено из MedicalRecordsPanelComponent): заголовок, чипы-метаданные,
 * «Открыть» и меню «…» на десктопе, блок распознавания. Клик по всей строке открывает запись
 * отдельной страницей на любой ширине экрана.
 */
@Component({
  selector: 'app-record-list-card',
  standalone: true,
  imports: [ActionMenuComponent, RecordRecognitionStatusComponent],
  template: `
    <div class="card record-card flex-fill">
      <div class="record-card-row clickable" (click)="open.emit()">
        <div class="record-card-main">
          <div class="card-title" style="font-size:var(--font-size-record-title)">{{ shortName() }}</div>

          <div class="record-chips">
            @if (record().kindIsAutoDetected) {
              <!-- Пакетная загрузка — вид ещё не определён пайплайном; снимается после распознавания. -->
              <span class="tag tag-neutral" title="Вид записи будет уточнён после распознавания">
                <i class="ph ph-magic-wand" aria-hidden="true"></i>Вид определяется автоматически
              </span>
            }
            @if (record().attachmentCount > 0) {
              <span class="tag tag-neutral"><i class="ph ph-paperclip" aria-hidden="true"></i>{{ attachmentLabel() }}</span>
            }
            @if (record().indicatorCount > 0) {
              <span class="tag tag-neutral"><i class="ph ph-list-checks" aria-hidden="true"></i>{{ indicatorLabel() }}</span>
            }
            @if (record().abnormalIndicatorCount > 0) {
              <span class="tag" style="background:color-mix(in srgb, var(--color-status-danger) 22%, var(--color-paper));color:var(--color-status-danger-text)">
                <i class="ph-fill ph-warning-circle" aria-hidden="true"></i>{{ record().abnormalIndicatorCount }} вне нормы
              </span>
            } @else if (record().normalIndicatorCount > 0) {
              <span class="tag" style="background:color-mix(in srgb, var(--color-status-ok) 22%, var(--color-paper));color:var(--color-status-ok-text)">
                <i class="ph-fill ph-check-circle" aria-hidden="true"></i>{{ record().normalIndicatorCount }} в норме
              </span>
            }
            @if (record().doctor) {
              <span class="tag tag-neutral">
                <i class="ph ph-stethoscope" aria-hidden="true"></i>Врач: {{ record().doctor }}
              </span>
            }
          </div>
        </div>

        @if (wide()) {
          <!-- stopPropagation — иначе клик по кнопке/меню ещё и всплывал бы до строки. -->
          <div class="record-card-actions" (click)="$event.stopPropagation()">
            <button class="btn btn-secondary text-nowrap" (click)="open.emit()">Открыть</button>
            <app-action-menu [actions]="actions()" label="Действия с записью" />
          </div>
        }
      </div>

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
    :host { display: flex; flex: 1 1 auto; min-width: 0; }

    // Чипы-метаданные записи вместо безымянных иконок.
    .record-chips {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 8px;
      margin: 6px 0 10px;

      .tag i {
        font-size: 1em;
        margin-right: 4px;
      }
    }

    // Заголовок+чипы слева, «Открыть»/«…» справа, одной строкой.
    .record-card-row {
      display: flex;
      align-items: flex-start;
      gap: 16px;

      &.clickable { cursor: pointer; }
    }

    .record-card-main {
      flex: 1;
      min-width: 0;
    }

    .record-card-actions {
      flex: none;
      display: flex;
      align-items: center;
      gap: 8px;
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

  protected shortName(): string {
    return recordShortName(this.record());
  }

  protected attachmentLabel(): string {
    return attachmentCountLabel(this.record());
  }

  protected indicatorLabel(): string {
    return indicatorCountLabel(this.record());
  }
}
