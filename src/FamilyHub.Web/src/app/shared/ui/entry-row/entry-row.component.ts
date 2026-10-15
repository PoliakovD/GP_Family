import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import type { IconNode as LucideIconData } from 'lucide';
import { IconTileComponent } from '../icon-tile/icon-tile.component';
import { Tone } from '../tone';

/**
 * Строка записи дневника (DESIGN §3 «entry-row»): время (14, tabular, ширина 48) → icon-tile вида →
 * заголовок + подпись → «⋯» (контентом). На мобайле время уходит в подпись.
 *
 * С `actionLabel` плитка и текст — кнопка (aria-label = actionLabel), клик — `activated`; «⋯» и бейджи
 * остаются отдельными элементами рядом, не внутри кнопки.
 */
@Component({
  selector: 'app-entry-row',
  standalone: true,
  imports: [IconTileComponent, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="er-time">{{ time() }}</span>
    @if (actionLabel(); as al) {
      <button type="button" class="er-main er-main-btn" [attr.aria-label]="al" (click)="activated.emit()">
        <ng-container *ngTemplateOutlet="main" />
      </button>
    } @else {
      <span class="er-main"><ng-container *ngTemplateOutlet="main" /></span>
    }
    <ng-content />
    <ng-template #main>
      <app-icon-tile [icon]="icon()" [tone]="tone()" />
      <span class="er-text">
        <span class="er-title">{{ title() }}</span>
        <span class="er-sub">
          <span class="er-time-inline">{{ time() }}@if (subtitle()) {<span> · </span>}</span>{{ subtitle() }}
        </span>
      </span>
    </ng-template>
  `,
  styles: [`
    :host {
      position: relative;
      display: flex;
      align-items: center;
      gap: var(--space-4);
      min-height: 68px;
      padding: var(--space-3) var(--space-6);
    }
    /* Разделитель с отступами по бокам (DESIGN §3 «list-card»). */
    :host(:not(:first-of-type))::before { content: ''; position: absolute; top: 0; left: var(--space-6); right: var(--space-6); border-top: 1px solid var(--color-divider); }
    @media (max-width: 639px) { :host(:not(:first-of-type))::before { left: var(--space-4); right: var(--space-4); } }
    .er-time {
      flex: none;
      width: 48px;
      font: var(--text-caption);
      font-variant-numeric: tabular-nums;
      color: var(--color-text-muted);
    }
    .er-main { display: flex; align-items: center; gap: var(--space-4); flex: 1; min-width: 0; }
    .er-main-btn {
      padding: 0;
      border: 0;
      background: none;
      font: inherit;
      color: inherit;
      text-align: left;
      cursor: pointer;
      border-radius: var(--radius-md);
    }
    @media (hover: hover) { :host(:has(.er-main-btn):hover) { background: var(--color-surface-muted); } }
    .er-text { display: flex; flex-direction: column; gap: 2px; flex: 1; min-width: 0; }
    .er-title { font: var(--text-body); font-weight: 600; }
    .er-sub { font: var(--text-caption); color: var(--color-text-muted); }
    .er-time-inline { display: none; font-variant-numeric: tabular-nums; }
    @media (max-width: 639px) {
      :host { gap: var(--space-3); padding: var(--space-3) var(--space-4); }
      .er-main { gap: var(--space-3); }
      .er-time { display: none; }
      .er-time-inline { display: inline; }
    }
  `],
})
export class EntryRowComponent {
  readonly time = input.required<string>();
  readonly icon = input.required<LucideIconData>();
  readonly tone = input<Tone>('note');
  readonly title = input.required<string>();
  readonly subtitle = input<string | null>(null);
  /** Есть — строка открывается (кнопка с этим aria-label); нет — только чтение. */
  readonly actionLabel = input<string | null>(null);
  readonly activated = output<void>();
}
