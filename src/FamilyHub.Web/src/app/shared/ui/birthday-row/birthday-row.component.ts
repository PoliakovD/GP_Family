import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * Строка дня рождения (DESIGN §3 «Строки»): плитка даты 48px «24 / ноя» → имя 16/600 + подпись 14 muted
 * («Исполнится 49»). Ближайший — `soon`: акцентная плитка (макет home-desktop). Главная, лендинг.
 */
@Component({
  selector: 'app-birthday-row',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="bd-date" [class.bd-soon]="soon()">
      <span class="bd-day">{{ day() }}</span>
      <span class="bd-month">{{ month() }}</span>
    </span>
    <span class="bd-text">
      <span class="bd-name">{{ name() }}</span>
      <span class="bd-sub">{{ subtitle() }}</span>
    </span>
  `,
  styles: [`
    :host { display: flex; align-items: center; gap: var(--space-3); }
    .bd-date {
      display: inline-flex;
      flex: none;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      width: 48px;
      height: 48px;
      border-radius: var(--radius-lg);
      background: var(--color-surface-sunken);
      color: var(--color-text);
    }
    .bd-soon { background: var(--color-accent-soft); color: var(--color-accent-hover); }
    .bd-day { font: var(--text-title); font-weight: 700; line-height: 1; font-variant-numeric: tabular-nums; }
    .bd-month { font: var(--text-micro); }
    .bd-text { display: flex; flex-direction: column; min-width: 0; }
    .bd-name { font: var(--text-body); font-weight: 600; }
    .bd-sub { font: var(--text-caption); color: var(--color-text-muted); }
  `],
})
export class BirthdayRowComponent {
  readonly day = input.required<string | number>();
  readonly month = input.required<string>();
  readonly name = input.required<string>();
  readonly subtitle = input<string>('');
  readonly soon = input(false);
}
