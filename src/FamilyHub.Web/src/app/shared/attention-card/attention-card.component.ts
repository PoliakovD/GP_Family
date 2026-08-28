import { Component, input } from '@angular/core';

/**
 * Каркас карточки «Требует внимания» (редизайн v2, Главная) — цветная полоса слева по
 * срочности + иконка + заголовок/подзаголовок + слот действий. Владеет только версткой-рамкой;
 * три разных набора контента/кнопок (лекарства/заявки/ДР) остаются `@switch`-ветками в
 * home.component.html — обобщать разные наборы действий в один компонент преждевременно (см.
 * план редизайна, PR3a).
 */
@Component({
  selector: 'app-attention-card',
  standalone: true,
  template: `
    <div class="attention-card" [style.border-left-color]="color()">
      <i [class]="icon()" [style.color]="color()" aria-hidden="true"></i>
      <div class="attention-card-body">
        <div class="attention-card-title">{{ title() }}</div>
        @if (subtitle()) {
          <div class="attention-card-subtitle">{{ subtitle() }}</div>
        }
        <div class="attention-card-actions">
          <ng-content select="[actions]" />
        </div>
      </div>
    </div>
  `,
  styleUrl: './attention-card.component.scss',
})
export class AttentionCardComponent {
  /** Класс Phosphor-иконки, например "ph-duotone ph-pill". */
  readonly icon = input.required<string>();
  /** Значение CSS-цвета (обычно var(--color-status-*) или var(--color-accent[-2])). */
  readonly color = input.required<string>();
  readonly title = input.required<string>();
  readonly subtitle = input<string | null>(null);
}
