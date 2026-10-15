import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ChevronRight, type LucideIconData } from 'lucide-angular';
import { IconComponent } from '../icon/icon.component';
import { IconTileComponent } from '../icon-tile/icon-tile.component';
import { Tone } from '../tone';

/**
 * Строка-переход (DESIGN §3 «nav-row»): иконка → текст + подпись → шеврон, вся строка — ссылка,
 * высота ≥ 56px. Справа перед шевроном можно положить бейдж/счётчик контентом.
 */
@Component({
  selector: 'app-nav-row',
  standalone: true,
  imports: [RouterLink, IconComponent, IconTileComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="nr" [routerLink]="link()">
      @if (icon(); as ic) { <app-icon-tile [icon]="ic" [tone]="tone()" size="sm" /> }
      <span class="nr-text">
        <span class="nr-title">{{ title() }}</span>
        @if (subtitle(); as s) { <span class="nr-sub">{{ s }}</span> }
      </span>
      <ng-content />
      <app-icon class="nr-chevron" [icon]="chevron" [size]="18" />
    </a>
  `,
  styles: [`
    :host { display: block; }
    /* Разделитель с отступами по бокам (DESIGN §3 «list-card»). */
    :host(:not(:first-of-type)) .nr::before { content: ''; position: absolute; top: 0; left: var(--space-6); right: var(--space-6); border-top: 1px solid var(--color-divider); }
    @media (max-width: 639px) { :host(:not(:first-of-type)) .nr::before { left: var(--space-4); right: var(--space-4); } }
    .nr {
      position: relative;
      display: flex;
      align-items: center;
      gap: var(--space-3);
      min-height: 56px;
      padding: var(--space-2) var(--space-6);
      color: var(--color-text);
      text-decoration: none;
      transition: background-color 0.15s ease;
    }
    @media (hover: hover) { .nr:hover { background: var(--color-surface-muted); } }
    .nr:focus-visible { outline-offset: -2px; }
    .nr-text { display: flex; flex-direction: column; gap: 2px; flex: 1; min-width: 0; }
    .nr-title { font: var(--text-body); font-weight: 600; }
    .nr-sub { font: var(--text-caption); color: var(--color-text-muted); }
    .nr-chevron { color: var(--color-text-muted); }
    @media (max-width: 639px) { .nr { padding: var(--space-2) var(--space-4); } }
  `],
})
export class NavRowComponent {
  readonly link = input.required<string | readonly unknown[]>();
  readonly title = input.required<string>();
  readonly subtitle = input<string | null>(null);
  readonly icon = input<LucideIconData | null>(null);
  readonly tone = input<Tone>('neutral');

  protected readonly chevron = ChevronRight;
}
