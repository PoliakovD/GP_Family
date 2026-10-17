import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { IconNode as LucideIconData } from 'lucide';
import { BackLinkComponent } from '../../back-link/back-link.component';
import { IconTileComponent } from '../icon-tile/icon-tile.component';
import type { Tone } from '../tone';

/**
 * Вид «страницы в колонке контента» (DESIGN §3 «Страница формы»): «‹ Назад к разделу» → (плитка) подпись
 * 14 muted + H1 → содержимое в карточке. Так на десктопе открываются формы добавления и статьи
 * справочника — как открытая запись, а не панелью справа. Используют каркас (страницы из
 * ContentPageService) и страницы-роуты добавления анализа/приёма.
 */
@Component({
  selector: 'app-content-page',
  standalone: true,
  imports: [BackLinkComponent, IconTileComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[style.--content-page-max]': 'maxWidth()' },
  template: `
    <app-back-link [label]="backLabel()" (activated)="back.emit()" />
    <header class="cp-head">
      @if (icon(); as i) { <app-icon-tile [icon]="i" [tone]="tone()" size="lg" /> }
      <div class="cp-heading">
        @if (kicker(); as k) { <span class="cp-kicker">{{ k }}</span> }
        <h1 class="page-title cp-title">{{ title() }}</h1>
      </div>
    </header>
    <div class="cp-body">
      <ng-content />
    </div>
  `,
  styles: [`
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
      width: 100%;
      max-width: var(--content-page-max, 760px);
    }
    .cp-head { display: flex; align-items: center; gap: var(--space-4); }
    .cp-heading { display: flex; flex-direction: column; gap: var(--space-1); min-width: 0; }
    .cp-kicker { font: var(--text-caption); font-weight: 600; color: var(--color-text-muted); }
    .cp-title { overflow-wrap: anywhere; }
    /* Содержимое — карточкой: формы со своим низом (кнопки) и статьи одинаково. */
    .cp-body {
      display: flex;
      flex-direction: column;
      gap: var(--space-5);
      padding: var(--space-6);
      border-radius: var(--radius-2xl);
      background: var(--color-surface);
    }
  `],
})
export class ContentPageComponent {
  readonly title = input.required<string>();
  readonly kicker = input<string | null>(null);
  readonly backLabel = input('Назад');
  readonly icon = input<LucideIconData | null>(null);
  readonly tone = input<Tone>('neutral');
  readonly maxWidth = input('760px');
  readonly back = output<void>();
}
