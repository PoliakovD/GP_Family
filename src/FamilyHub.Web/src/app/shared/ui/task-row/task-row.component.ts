import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ChevronRight, type LucideIconData } from 'lucide-angular';
import { IconComponent } from '../icon/icon.component';
import { IconTileComponent } from '../icon-tile/icon-tile.component';
import { Tone } from '../tone';

/**
 * Строка «Требует внимания» (DESIGN §3 «task-row»): icon-tile → заголовок 16/600 + подпись 14 muted →
 * кнопка (первая в списке — inverse, остальные — secondary).
 *
 * На узком экране (<1024) кнопки нет: вместо неё шеврон, а вся строка — ссылка (`link`) или, если
 * ссылки нет, кнопка с текстом действия в aria-label. Действие — `link` (переход) или `(action)`.
 */
@Component({
  selector: 'app-task-row',
  standalone: true,
  imports: [RouterLink, IconComponent, IconTileComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-icon-tile [icon]="icon()" [tone]="tone()" />
    <div class="tr-text">
      @if (link(); as href) {
        <a class="tr-title tr-stretch" [routerLink]="href">{{ title() }}</a>
      } @else {
        <span class="tr-title">{{ title() }}</span>
      }
      @if (subtitle(); as s) { <span class="tr-sub">{{ s }}</span> }
    </div>
    @if (actionLabel(); as label) {
      @if (link(); as href) {
        <a class="btn tr-action" [class.btn-inverse]="primary()" [class.btn-secondary]="!primary()"
           [routerLink]="href" tabindex="-1" aria-hidden="true">{{ label }}</a>
      } @else {
        <button type="button" class="btn tr-action" [class.btn-inverse]="primary()" [class.btn-secondary]="!primary()"
                (click)="action.emit()">{{ label }}</button>
        <button type="button" class="btn-icon tr-chevron-btn" [attr.aria-label]="label + ': ' + title()" (click)="action.emit()">
          <app-icon [icon]="chevron" [size]="18" />
        </button>
      }
    }
    @if (link()) { <app-icon class="tr-chevron" [icon]="chevron" [size]="18" /> }
  `,
  styles: [`
    :host {
      position: relative;
      display: flex;
      align-items: center;
      gap: var(--space-4);
      min-height: 72px;
      padding: var(--space-3) var(--space-6);
    }
    /* Разделитель с отступами 24 по бокам (DESIGN §3 «list-card»). */
    :host(:not(:first-of-type))::before { content: ''; position: absolute; top: 0; left: var(--space-6); right: var(--space-6); border-top: 1px solid var(--color-divider); }
    @media (max-width: 1023px) { :host(:not(:first-of-type))::before { left: var(--space-4); right: var(--space-4); } }
    .tr-text { display: flex; flex-direction: column; gap: 2px; flex: 1; min-width: 0; }
    .tr-title { font: var(--text-body); font-weight: 600; color: var(--color-text); text-decoration: none; }
    .tr-sub { font: var(--text-caption); color: var(--color-text-muted); }
    .tr-action { flex: none; }
    .tr-chevron, .tr-chevron-btn { display: none; color: var(--color-text-muted); }

    /* На десктопе ссылка — заголовок и кнопка; фокус получает заголовок (кнопка-дубль скрыта от AT). */
    @media (max-width: 1023px) {
      :host { gap: var(--space-3); padding: var(--space-3) var(--space-4); min-height: 64px; }
      .tr-action { display: none; }
      .tr-chevron, .tr-chevron-btn { display: inline-flex; flex: none; }
      /* Вся строка — ссылка: псевдоэлемент растягивает область нажатия заголовка. */
      .tr-stretch::after { content: ''; position: absolute; inset: 0; }
      .tr-stretch:focus-visible { outline: none; }
      .tr-stretch:focus-visible::after { outline: 2px solid var(--color-accent); outline-offset: -2px; border-radius: var(--radius-md); }
    }
  `],
})
export class TaskRowComponent {
  readonly icon = input.required<LucideIconData>();
  readonly tone = input<Tone>('neutral');
  readonly title = input.required<string>();
  readonly subtitle = input<string | null>(null);
  readonly actionLabel = input<string | null>(null);
  /** Главное действие списка — тёмная (inverse) кнопка; остальные строки — secondary. */
  readonly primary = input(false);
  readonly link = input<string | readonly unknown[] | null>(null);
  readonly action = output<void>();

  protected readonly chevron = ChevronRight;
}
