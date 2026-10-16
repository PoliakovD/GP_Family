import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { Monitor, Moon, Sun } from 'lucide';
import type { IconNode as LucideIconData } from 'lucide';
import { ThemePreference, ThemeService } from '../../../services/theme.service';
import { IconComponent } from '../icon/icon.component';

let nextId = 0;

/**
 * Выбор темы «Системная / Светлая / Тёмная» (DESIGN §2.2) — `.seg` из радиокнопок, выбор на устройство
 * (ThemeService). `compact` — только иконки с подписью для AT и подсказкой (шапка админки);
 * без него — иконка + подпись («Профиль → Аккаунт → Оформление»), на телефоне подписи без иконок.
 */
@Component({
  selector: 'app-theme-switch',
  standalone: true,
  imports: [IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.ts-compact]': 'compact()' },
  template: `
    <div class="seg ts" role="radiogroup" [attr.aria-label]="label()">
      @for (opt of options; track opt.value) {
        <label class="seg-opt" [attr.title]="compact() ? opt.label : null">
          <input type="radio" [name]="name" [checked]="theme.preference() === opt.value" (change)="theme.set(opt.value)"
                 [attr.aria-label]="compact() ? opt.label : null" />
          <app-icon class="ts-icon" [icon]="opt.icon" [size]="18" />
          @if (!compact()) { <span>{{ opt.label }}</span> }
        </label>
      }
    </div>
  `,
  styles: [`
    :host { display: block; max-width: 100%; }
    .ts { max-width: 100%; }
    :host(.ts-compact) .seg-opt { padding-inline: var(--space-3); }
    @media (max-width: 639px) {
      :host(:not(.ts-compact)) .ts { display: flex; width: 100%; }
      :host(:not(.ts-compact)) .seg-opt { flex: 1; justify-content: center; }
      :host(:not(.ts-compact)) .ts-icon { display: none; }
    }
  `],
})
export class ThemeSwitchComponent {
  readonly compact = input(false);
  readonly label = input('Тема оформления');

  protected readonly theme = inject(ThemeService);
  protected readonly name = `theme-switch-${++nextId}`;
  protected readonly options: { value: ThemePreference; label: string; icon: LucideIconData }[] = [
    { value: 'system', label: 'Системная', icon: Monitor },
    { value: 'light', label: 'Светлая', icon: Sun },
    { value: 'dark', label: 'Тёмная', icon: Moon },
  ];
}
