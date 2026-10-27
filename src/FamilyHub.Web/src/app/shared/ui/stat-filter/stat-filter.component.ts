import { ChangeDetectionStrategy, Component, input, model } from '@angular/core';

/**
 * Счётчик-фильтр анализа (DESIGN §3 «Анализ»): «6 вне нормы / 14 в норме / 0 без нормы» —
 * `button` с aria-pressed; активный — рамка 2px цвета статуса + мягкий фон.
 */
@Component({
  selector: 'app-stat-filter',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button type="button" class="sf" [class]="'sf tone-' + tone()" [attr.aria-pressed]="pressed()"
            (click)="pressed.set(!pressed())">
      <span class="sf-value">{{ value() }}</span>
      <span class="sf-label">{{ label() }}</span>
    </button>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .sf {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      gap: var(--space-1);
      width: 100%;
      min-height: 96px;
      padding: var(--space-4) var(--space-5);
      font: inherit;
      text-align: left;
      color: var(--color-text);
      background: var(--color-surface);
      border: 2px solid transparent;
      border-radius: var(--radius-xl);
      cursor: pointer;
      transition: background-color 0.15s ease, border-color 0.15s ease;
    }
    @media (hover: hover) { .sf[aria-pressed="false"]:hover { background: var(--color-surface-muted); } }
    .sf[aria-pressed="true"] { border-color: var(--tone-fg); background: var(--tone-bg); }
    .sf-value { font: var(--text-metric); font-variant-numeric: tabular-nums; color: var(--tone-fg); }
    .sf-label { font: var(--text-body-sm); }
    .tone-neutral .sf-value { color: var(--color-text-muted); }
    @media (max-width: 639px) {
      .sf { min-height: 72px; padding: var(--space-3); border-radius: var(--radius-lg); }
      .sf-value { font: var(--text-metric-mobile); }
      .sf-label { font: var(--text-caption); }
    }
  `],
})
export class StatFilterComponent {
  readonly value = input.required<number | string>();
  readonly label = input.required<string>();
  readonly tone = input<'danger' | 'success' | 'neutral'>('neutral');
  readonly pressed = model(false);
}
