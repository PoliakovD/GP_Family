import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { IconNode as LucideIconData } from 'lucide';
import { IconComponent } from '../icon/icon.component';
import { Tone } from '../tone';

/**
 * Плитка-иконка (DESIGN §3 «icon-tile»): 40–44px, radius-lg, фон *-soft, иконка цвета статуса/вида.
 * Цвета полос слева у карточек больше нет — вид записи показывает эта плитка.
 */
@Component({
  selector: 'app-icon-tile',
  standalone: true,
  imports: [IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class]': '"icon-tile tone-" + tone() + " icon-tile-" + size()',
  },
  template: `<app-icon [icon]="icon()" [size]="size() === 'sm' ? 18 : 20" />`,
  styles: [`
    :host {
      display: inline-flex;
      flex: none;
      align-items: center;
      justify-content: center;
      border-radius: var(--radius-lg);
      background: var(--tone-bg);
      color: var(--tone-fg);
    }
    :host(.icon-tile-sm) { width: 36px; height: 36px; border-radius: var(--radius-md); }
    :host(.icon-tile-md) { width: 40px; height: 40px; }
    :host(.icon-tile-lg) { width: 44px; height: 44px; }
  `],
})
export class IconTileComponent {
  readonly icon = input.required<LucideIconData>();
  readonly tone = input<Tone>('neutral');
  readonly size = input<'sm' | 'md' | 'lg'>('md');
}
