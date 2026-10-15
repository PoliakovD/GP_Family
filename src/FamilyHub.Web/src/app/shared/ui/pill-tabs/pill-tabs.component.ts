import { ChangeDetectionStrategy, Component, ElementRef, inject, input, model } from '@angular/core';

export interface PillTab {
  id: string;
  label: string;
  /** Счётчик внутри пилюли (непрочитанное и т.п.); 0/undefined — не показывается. */
  count?: number | null;
}

/**
 * Вкладки-пилюли (DESIGN §3 «tabs-pill»): разделы семьи, фильтры дневника. Пилюли 40–42px,
 * активная — inverse, остальные — surface + рамка; счётчик внутри; на мобайле — горизонтальный
 * скролл. Роль tablist/tab, стрелки ←/→ переводят фокус и выбор.
 */
@Component({
  selector: 'app-pill-tabs',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { role: 'tablist', '[attr.aria-label]': 'label()', '(keydown)': 'onKey($event)' },
  template: `
    @for (tab of tabs(); track tab.id) {
      <button type="button" role="tab" class="pt" [class.pt-active]="tab.id === value()"
              [attr.aria-selected]="tab.id === value()" [attr.tabindex]="tab.id === value() ? 0 : -1"
              (click)="value.set(tab.id)">
        {{ tab.label }}
        @if (tab.count) {
          <span class="count-badge count-badge-accent pt-count" [attr.aria-label]="tab.count + ' новых'">{{ tab.count }}</span>
        }
      </button>
    }
  `,
  styles: [`
    :host {
      display: flex;
      gap: var(--space-2);
      overflow-x: auto;
      scrollbar-width: none;
      -webkit-overflow-scrolling: touch;
      padding: 2px; /* фокус-кольцо не обрезается скроллом */
    }
    :host::-webkit-scrollbar { display: none; }
    .pt {
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      flex: none;
      min-height: 42px;
      padding: 0 var(--space-4);
      font: var(--text-button);
      font-weight: 500;
      white-space: nowrap;
      color: var(--color-text);
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius-pill);
      cursor: pointer;
      transition: background-color 0.15s ease, border-color 0.15s ease;
    }
    @media (hover: hover) { .pt:not(.pt-active):hover { border-color: var(--color-border-dashed); background: var(--color-surface-muted); } }
    .pt-active { background: var(--color-inverse); border-color: var(--color-inverse); color: var(--color-on-accent); font-weight: 600; }
    @media (pointer: coarse) { .pt { min-height: var(--tap-min); } }
  `],
})
export class PillTabsComponent {
  readonly tabs = input.required<readonly PillTab[]>();
  readonly value = model.required<string>();
  readonly label = input<string | null>(null);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected onKey(e: KeyboardEvent): void {
    if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
    const tabs = this.tabs();
    const i = tabs.findIndex((t) => t.id === this.value());
    const next = tabs[(i + (e.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length];
    this.value.set(next.id);
    e.preventDefault();
    queueMicrotask(() => {
      const btn = this.host.nativeElement.querySelector<HTMLButtonElement>('.pt-active');
      btn?.focus();
      btn?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
    });
  }
}
