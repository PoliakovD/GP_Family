import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { AvatarComponent, AvatarPerson } from '../../avatar/avatar.component';

/**
 * Строка участника (DESIGN §3 «person-row»): аватар 44 → имя 16/600 + роль 14 muted → меню «⋯»
 * (контентом, например `<app-action-menu>`). Индикатор «в сети» — точка success 11px с обводкой.
 */
@Component({
  selector: 'app-person-row',
  standalone: true,
  imports: [AvatarComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="pr-avatar">
      <app-avatar [person]="person()" [self]="self()" size="lg" />
      @if (online()) { <span class="pr-online" role="img" aria-label="в сети"></span> }
    </span>
    <span class="pr-text">
      <span class="pr-name">{{ name() }}</span>
      @if (role(); as r) { <span class="pr-role">{{ r }}</span> }
    </span>
    <ng-content />
  `,
  styles: [`
    :host { display: flex; align-items: center; gap: var(--space-3); min-height: 60px; padding: var(--space-2) 0; }
    .pr-avatar { position: relative; display: inline-flex; flex: none; }
    .pr-online {
      position: absolute;
      right: -1px;
      bottom: -1px;
      width: 11px;
      height: 11px;
      border-radius: var(--radius-pill);
      background: var(--color-success);
      box-shadow: 0 0 0 2px var(--color-surface);
    }
    .pr-text { display: flex; flex-direction: column; gap: 2px; flex: 1; min-width: 0; }
    .pr-name { font: var(--text-body); font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .pr-role { font: var(--text-caption); color: var(--color-text-muted); }
  `],
})
export class PersonRowComponent {
  readonly person = input.required<AvatarPerson>();
  readonly name = input.required<string>();
  readonly role = input<string | null>(null);
  readonly online = input(false);
  readonly self = input(false);
}
