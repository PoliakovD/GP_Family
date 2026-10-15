import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { RouterLink } from '@angular/router';

/**
 * Карточка метрики (DESIGN §3 «metric-card»): подпись (15px muted) → число (metric, tabular) →
 * бейдж или подпись. С `link` вся карточка — ссылка.
 *
 * Бейдж передаётся контентом: `<app-metric-card label="Давление" value="122/95">
 *   <span badge class="badge badge-warning">выше рекомендуемого</span></app-metric-card>`.
 */
@Component({
  selector: 'app-metric-card',
  standalone: true,
  imports: [NgTemplateOutlet, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (link(); as href) {
      <a class="mc mc-link" [routerLink]="href"><ng-container *ngTemplateOutlet="body" /></a>
    } @else {
      <div class="mc"><ng-container *ngTemplateOutlet="body" /></div>
    }
    <ng-template #body>
      <span class="mc-label">{{ label() }}</span>
      <span class="mc-value">
        <span class="mc-number">{{ value() }}</span>
        @if (unit(); as u) { <span class="mc-unit">{{ u }}</span> }
      </span>
      <span class="mc-foot">
        <ng-content select="[badge]" />
        @if (caption(); as c) { <span class="mc-caption">{{ c }}</span> }
      </span>
      <ng-content />
    </ng-template>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .mc {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      height: 100%;
      padding: var(--space-5);
      border-radius: var(--radius-xl);
      background: var(--color-surface);
      color: var(--color-text);
      text-decoration: none;
    }
    .mc-link { transition: background-color 0.15s ease; }
    @media (hover: hover) { .mc-link:hover { background: var(--color-surface-muted); } }
    .mc-label { font: var(--text-body-sm); color: var(--color-text-muted); }
    .mc-value { display: flex; align-items: baseline; gap: var(--space-2); }
    .mc-number {
      font: var(--text-metric);
      letter-spacing: var(--letter-spacing-display);
      font-variant-numeric: tabular-nums;
    }
    .mc-unit { font: var(--text-body-sm); color: var(--color-text-muted); }
    .mc-foot { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); min-height: 24px; }
    .mc-foot:empty { display: none; }
    .mc-caption { font: var(--text-caption); color: var(--color-text-muted); }
    @media (max-width: 639px) {
      .mc { padding: var(--space-4); }
      .mc-number { font: var(--text-metric-mobile); }
    }
  `],
})
export class MetricCardComponent {
  readonly label = input.required<string>();
  readonly value = input.required<string | number>();
  readonly unit = input<string | null>(null);
  readonly caption = input<string | null>(null);
  readonly link = input<string | readonly unknown[] | null>(null);
}
