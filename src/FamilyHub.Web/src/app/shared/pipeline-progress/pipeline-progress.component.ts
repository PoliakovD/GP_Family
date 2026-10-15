import { Component, Input, ChangeDetectionStrategy } from '@angular/core';
import { Check, X } from 'lucide';
import { IconComponent } from '../ui/icon/icon.component';

export interface PipelineStep {
  id: string;
  label: string;
  state: 'done' | 'active' | 'error';
}

/**
 * Живой прогресс LLM-конвейера (UX-редизайн) — вертикальный растущий список вместо
 * горизонтального степпера `○—○—○` (такой уже есть в `medications-panel.component.scss`, но не
 * подходит: шесть русских подписей ExtractionStage не влезают в 360px и не показывают историю).
 * Выполненные шаги остаются с галочкой, текущий пульсирует в стиле «Thinking…» с бегущим
 * многоточием. Будущие шаги НЕ показываются — конвейер может их пропустить (текстовый путь не
 * заходит в OCR), серые «ещё не сделано» пункты создавали бы ложное обещание.
 *
 * Только последние 4 строки видимы — старые «уезжают» за счёт `overflow:hidden` на контейнере
 * фиксированной высоты, не удаляются из DOM (иначе `@for`-track дёргал бы entrance-анимацию
 * заново на каждый чужой ре-рендер).
 */
@Component({
  selector: 'app-pipeline-progress',
  imports: [IconComponent],
  standalone: true,
  templateUrl: './pipeline-progress.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './pipeline-progress.component.scss',
})
export class PipelineProgressComponent {
  protected readonly icons = { Check, X };

  @Input() steps: PipelineStep[] = [];
}
