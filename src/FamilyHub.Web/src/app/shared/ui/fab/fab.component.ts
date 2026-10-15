import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { Plus, type LucideIconData } from 'lucide-angular';
import { IconComponent } from '../icon/icon.component';

/**
 * Плавающая кнопка «Добавить» (DESIGN §3 «fab»): только < 1024px, 60px, radius 20, accent,
 * --shadow-fab, над таб-баром справа (bottom 100px). Подпись — только в aria-label.
 * Заменяет построчные копии .dr-fab / .dn-fab.
 */
@Component({
  selector: 'app-fab',
  standalone: true,
  imports: [IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button type="button" class="fab" [attr.aria-label]="label()" (click)="activated.emit()">
      <app-icon [icon]="icon()" [size]="26" [strokeWidth]="2" />
    </button>
  `,
  styles: [`
    :host { display: none; }
    @media (max-width: 1023px) { :host { display: contents; } }
    .fab {
      position: fixed;
      right: var(--space-4);
      bottom: calc(100px + env(safe-area-inset-bottom, 0px));
      z-index: var(--z-fab);
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 60px;
      height: 60px;
      border: 0;
      border-radius: var(--radius-xl);
      background: var(--color-accent);
      color: var(--color-on-accent);
      box-shadow: var(--shadow-fab);
      cursor: pointer;
      transition: background-color 0.15s ease, transform 0.15s ease;
    }
    .fab:hover { background: var(--color-accent-hover); }
    .fab:active { transform: scale(0.96); }
  `],
})
export class FabComponent {
  readonly label = input.required<string>();
  readonly icon = input<LucideIconData>(Plus);
  readonly activated = output<void>();
}
