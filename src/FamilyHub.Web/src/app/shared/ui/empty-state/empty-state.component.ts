import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { IconNode as LucideIconData } from 'lucide';
import { IconTileComponent } from '../icon-tile/icon-tile.component';

/**
 * Пустое состояние (DESIGN §5): что это + одно действие. «Курсов пока нет. Добавьте лекарство —
 * напомним вовремя.» + кнопка контентом. Заменяет 7 локальных вариантов (аудит §4.6).
 * `inline` — однострочник внутри карточки, без иконки и рамки.
 */
@Component({
  selector: 'app-empty-state',
  standalone: true,
  imports: [IconTileComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.es-inline]': 'inline()' },
  template: `
    @if (icon() && !inline()) { <app-icon-tile [icon]="icon()!" tone="neutral" size="lg" /> }
    <div class="es-text">
      <p class="es-title">{{ title() }}</p>
      @if (text(); as t) { <p class="es-body">{{ t }}</p> }
    </div>
    <ng-content />
  `,
  styles: [`
    :host {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: var(--space-3);
      padding: var(--space-10) var(--space-5);
      text-align: center;
    }
    :host(.es-inline) { flex-direction: row; justify-content: space-between; padding: var(--space-4) 0; text-align: left; }
    .es-text { display: flex; flex-direction: column; gap: var(--space-1); max-width: 420px; }
    .es-title { margin: 0; font: var(--text-body); font-weight: 600; }
    .es-body { margin: 0; font: var(--text-body-sm); color: var(--color-text-muted); }
  `],
})
export class EmptyStateComponent {
  readonly title = input.required<string>();
  readonly text = input<string | null>(null);
  readonly icon = input<LucideIconData | null>(null);
  readonly inline = input(false);
}
