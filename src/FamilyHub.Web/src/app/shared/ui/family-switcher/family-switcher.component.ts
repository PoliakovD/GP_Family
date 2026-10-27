import { ChangeDetectionStrategy, Component, ElementRef, HostListener, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Check, ChevronDown, ChevronsUpDown, Plus } from 'lucide';
import { FamilyStateService } from '../../../services/family-state.service';
import { IconComponent } from '../icon/icon.component';

/**
 * Переключатель семьи (DESIGN §3 «Навигация»): в сайдбаре — блок 52px (surface-muted, рамка,
 * плитка-инициал семьи, «Семья» + название, ⌃⌄) сразу под логотипом; в мобильной шапке главной —
 * пилюля. Список — всплывающее меню (Escape и клик мимо закрывают), «Все семьи» внизу.
 * Нет семьи — ссылка «Создать семью».
 */
@Component({
  selector: 'app-family-switcher',
  standalone: true,
  imports: [RouterLink, IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class]': '"fs fs-" + variant()' },
  template: `
    @if (state.selectedFamily(); as fam) {
      <button type="button" class="fs-trigger" [attr.aria-expanded]="open()" aria-haspopup="listbox"
              [attr.aria-label]="'Семья: ' + fam.name + '. Сменить семью'" (click)="open.set(!open())">
        <span class="fs-mark" aria-hidden="true">{{ initial(fam.name) }}</span>
        @if (variant() === 'block') {
          <span class="fs-text"><span class="fs-caption">Семья</span><span class="fs-name">{{ fam.name }}</span></span>
          <app-icon class="fs-chevron" [icon]="icons.ChevronsUpDown" [size]="18" />
        } @else {
          <span class="fs-name">{{ fam.name }}</span>
          <app-icon class="fs-chevron" [icon]="icons.ChevronDown" [size]="18" />
        }
      </button>

      @if (open()) {
        <div class="fs-menu" role="listbox" aria-label="Семьи">
          @for (f of state.activeFamilies(); track f.id) {
            <button type="button" class="fs-item" role="option" [attr.aria-selected]="f.id === fam.id" (click)="select(f.id)">
              <span class="fs-mark fs-mark-sm" aria-hidden="true">{{ initial(f.name) }}</span>
              <span class="fs-item-name">{{ f.name }}</span>
              @if (f.id === fam.id) { <app-icon class="fs-check" [icon]="icons.Check" [size]="18" /> }
            </button>
          }
          <a class="fs-item fs-all" routerLink="/families" (click)="open.set(false)">Все семьи</a>
        </div>
      }
    } @else {
      <a class="fs-trigger" routerLink="/families">
        <span class="fs-mark fs-mark-add" aria-hidden="true"><app-icon [icon]="icons.Plus" [size]="18" /></span>
        <span class="fs-name">Создать семью</span>
      </a>
    }
  `,
  styles: [`
    :host { position: relative; display: block; }
    .fs-trigger {
      display: flex;
      align-items: center;
      gap: var(--space-3);
      width: 100%;
      min-height: 52px;
      padding: var(--space-2) var(--space-3);
      font: inherit;
      color: var(--color-text);
      text-align: left;
      text-decoration: none;
      background: var(--color-surface-muted);
      border: 1px solid var(--color-border);
      border-radius: var(--radius-lg);
      cursor: pointer;
    }
    @media (hover: hover) { .fs-trigger:hover { border-color: var(--color-border-dashed); } }
    .fs-mark {
      display: inline-flex;
      flex: none;
      align-items: center;
      justify-content: center;
      width: 36px;
      height: 36px;
      border-radius: var(--radius-md);
      background: var(--color-avatar-warm);
      color: var(--color-avatar-warm-text);
      font: var(--text-body);
      font-weight: 700;
    }
    .fs-mark-sm { width: 32px; height: 32px; font: var(--text-body-sm); font-weight: 700; }
    .fs-mark-add { background: var(--color-accent-soft); color: var(--color-accent-hover); }
    .fs-text { display: flex; flex-direction: column; flex: 1; min-width: 0; }
    .fs-caption { font: var(--text-micro); font-weight: 400; color: var(--color-text-muted); }
    .fs-name { font: var(--text-body-sm); font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .fs-chevron { color: var(--color-text-muted); }

    /* Мобильная шапка главной: пилюля на surface. */
    :host(.fs-pill) { display: inline-block; max-width: 60vw; }
    :host(.fs-pill) .fs-trigger {
      min-height: var(--tap-min);
      padding: var(--space-1) var(--space-3) var(--space-1) var(--space-1);
      gap: var(--space-2);
      background: var(--color-surface);
      border-radius: var(--radius-pill);
    }
    :host(.fs-pill) .fs-mark { width: 34px; height: 34px; border-radius: var(--radius-pill); }

    .fs-menu {
      position: absolute;
      top: calc(100% + var(--space-1));
      left: 0;
      min-width: 100%;
      width: max-content;
      max-width: 320px;
      display: flex;
      flex-direction: column;
      padding: var(--space-1);
      background: var(--color-surface);
      border: 1px solid var(--color-divider);
      border-radius: var(--radius-lg);
      box-shadow: var(--shadow-overlay);
      z-index: var(--z-dropdown);
    }
    .fs-item {
      display: flex;
      align-items: center;
      gap: var(--space-3);
      min-height: var(--tap-min);
      padding: var(--space-1) var(--space-2);
      font: var(--text-body-sm);
      color: var(--color-text);
      text-align: left;
      text-decoration: none;
      background: none;
      border: 0;
      border-radius: var(--radius-md);
      cursor: pointer;
    }
    .fs-item:hover { background: var(--color-surface-muted); }
    .fs-item-name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .fs-check { color: var(--color-accent); }
    .fs-all { color: var(--color-accent-hover); font-weight: 600; border-top: 1px solid var(--color-divider); border-radius: 0; margin-top: var(--space-1); }
  `],
})
export class FamilySwitcherComponent {
  readonly variant = input<'block' | 'pill'>('block');
  readonly state = inject(FamilyStateService);
  protected readonly open = signal(false);
  protected readonly icons = { Check, ChevronDown, ChevronsUpDown, Plus };
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected initial(name: string): string {
    return name.trim().charAt(0).toUpperCase() || '?';
  }

  protected select(id: string): void {
    this.state.selectFamily(id);
    this.open.set(false);
  }

  @HostListener('document:click', ['$event'])
  protected onDocumentClick(e: MouseEvent): void {
    if (this.open() && !this.host.nativeElement.contains(e.target as Node)) this.open.set(false);
  }

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    this.open.set(false);
  }
}
