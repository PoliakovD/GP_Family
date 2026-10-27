import { ChangeDetectionStrategy, Component, input, model, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ArrowRight, ChevronRight, Plus, type IconNode as LucideIconData } from 'lucide';
import { AvatarComponent, AvatarPerson } from '../../avatar/avatar.component';
import { IconComponent } from '../icon/icon.component';
import { IconTileComponent } from '../icon-tile/icon-tile.component';
import { Tone } from '../tone';

/**
 * Части чата семьи (DESIGN §3 «Чат семьи»). Сам экран чата переписывается в фазе 4 (модуль
 * «Семья и чат»); здесь — кирпичи, из которых он собирается.
 */

/** Пузырь: входящий — bubble-in, аватар слева, имя над первым в серии; свой — accent, справа. */
@Component({
  selector: 'app-chat-bubble',
  standalone: true,
  imports: [AvatarComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.cb-mine]': 'mine()', '[class.cb-first]': 'first()' },
  template: `
    @if (!mine()) {
      <span class="cb-avatar">
        @if (last() && author(); as a) { <app-avatar [person]="a" size="sm" /> }
      </span>
    }
    <div class="cb-col">
      @if (!mine() && first() && authorName()) { <span class="cb-name">{{ authorName() }}</span> }
      <div class="cb-bubble"><ng-content /></div>
      @if (time()) {
        <span class="cb-meta">{{ time() }}@if (mine() && read()) { · прочитано }</span>
      }
    </div>
  `,
  styles: [`
    :host { display: flex; align-items: flex-end; gap: var(--space-2); max-width: 72%; }
    :host(.cb-mine) { margin-left: auto; justify-content: flex-end; }
    .cb-avatar { flex: none; width: 32px; margin-bottom: 20px; }
    .cb-col { display: flex; flex-direction: column; gap: var(--space-1); min-width: 0; }
    :host(.cb-mine) .cb-col { align-items: flex-end; }
    .cb-name { font: var(--text-micro); color: var(--color-text-secondary); margin-left: var(--space-1); }
    .cb-bubble {
      padding: 10px var(--space-4);
      font: var(--text-body);
      color: var(--color-text);
      background: var(--chat-bubble-in, var(--color-bubble-in));
      border-radius: 18px 18px 18px 6px;
      overflow-wrap: anywhere;
    }
    :host(.cb-mine) .cb-bubble {
      background: var(--color-accent);
      color: var(--color-on-accent);
      border-radius: 18px 18px 6px 18px;
    }
    .cb-meta { font: var(--text-micro); font-weight: 400; color: var(--color-text-muted); margin-inline: var(--space-1); }
    @media (max-width: 639px) {
      :host { max-width: 84%; }
      /* На мобайле чат лежит на bg — входящий пузырь белый. */
      .cb-bubble { background: var(--chat-bubble-in, var(--color-surface)); }
    }
  `],
})
export class ChatBubbleComponent {
  readonly mine = input(false);
  readonly author = input<AvatarPerson | null>(null);
  readonly authorName = input<string | null>(null);
  /** Первое сообщение в серии одного автора — над ним имя. */
  readonly first = input(true);
  /** Последнее в серии — у него аватар. */
  readonly last = input(true);
  readonly time = input<string | null>(null);
  readonly read = input(false);
}

/** Системное сообщение — по центру, пилюля surface-sunken 13px muted. */
@Component({
  selector: 'app-chat-system',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span><ng-content /></span>`,
  styles: [`
    :host { display: flex; justify-content: center; }
    span {
      padding: var(--space-1) var(--space-3);
      font: var(--text-badge);
      font-weight: 400;
      color: var(--color-text-muted);
      background: var(--color-surface-sunken);
      border-radius: var(--radius-pill);
      text-align: center;
    }
  `],
})
export class ChatSystemComponent {}

/** Разделитель дня — 13/600 muted по центру. */
@Component({
  selector: 'app-chat-day',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { role: 'separator' },
  template: `<ng-content />`,
  styles: [`:host { display: block; text-align: center; font: var(--text-badge); color: var(--color-text-muted); padding: var(--space-2) 0; }`],
})
export class ChatDayComponent {}

/** Карточка записи в чате (анализ, приём, аптечка): вложенная карточка с рамкой → ведёт на запись. */
@Component({
  selector: 'app-chat-record-card',
  standalone: true,
  imports: [RouterLink, IconComponent, IconTileComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="crc" [routerLink]="link()">
      <app-icon-tile [icon]="icon()" [tone]="tone()" />
      <span class="crc-text">
        <span class="crc-title">{{ title() }}</span>
        <span class="crc-sub">
          {{ date() }}@if (status()) { · <span class="crc-status" [class]="'tone-' + statusTone()">{{ status() }}</span> }
        </span>
      </span>
      <app-icon class="crc-chevron" [icon]="chevron" [size]="18" />
    </a>
  `,
  styles: [`
    :host { display: block; max-width: 320px; }
    .crc {
      display: flex;
      align-items: center;
      gap: var(--space-3);
      padding: var(--space-3) var(--space-4);
      color: var(--color-text);
      text-decoration: none;
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius-lg);
    }
    @media (hover: hover) { .crc:hover { border-color: var(--color-border-dashed); } }
    .crc-text { display: flex; flex-direction: column; gap: 2px; flex: 1; min-width: 0; }
    .crc-title { font: var(--text-body-sm); font-weight: 600; }
    .crc-sub { font: var(--text-caption); color: var(--color-text-muted); }
    .crc-status { font-weight: 600; color: var(--tone-fg); }
    .crc-chevron { color: var(--color-text-muted); }
  `],
})
export class ChatRecordCardComponent {
  readonly link = input.required<string | readonly unknown[]>();
  readonly icon = input.required<LucideIconData>();
  readonly tone = input<Tone>('danger');
  readonly title = input.required<string>();
  readonly date = input.required<string>();
  /** Статус текстом («6 вне нормы») — цвет по смыслу, как бейдж. */
  readonly status = input<string | null>(null);
  readonly statusTone = input<Tone>('danger');

  protected readonly chevron = ChevronRight;
}

/** Композер: «+» (прикрепить запись/файл) → поле surface-sunken → отправка accent; всё 44–46px. */
@Component({
  selector: 'app-chat-composer',
  standalone: true,
  imports: [FormsModule, IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="cc" (submit)="$event.preventDefault(); submit()">
      <button type="button" class="btn-icon cc-attach" aria-label="Прикрепить запись или файл" (click)="attach.emit()">
        <app-icon [icon]="plusIcon" />
      </button>
      <label class="visually-hidden" [for]="inputId">{{ placeholder() }}</label>
      <input class="cc-input" [id]="inputId" type="text" autocomplete="off" [placeholder]="placeholder()"
             [(ngModel)]="text" name="message" [disabled]="disabled()" />
      <button type="submit" class="cc-send" aria-label="Отправить" [disabled]="disabled() || !text().trim()">
        <app-icon [icon]="sendIcon" />
      </button>
    </form>
  `,
  styles: [`
    .cc { display: flex; align-items: center; gap: var(--space-2); }
    .cc-attach { flex: none; background: var(--color-surface-sunken); border-radius: var(--radius-md); }
    .cc-input {
      flex: 1;
      min-width: 0;
      min-height: var(--control-height);
      padding: 0 var(--space-4);
      font: var(--text-body);
      color: var(--color-text);
      background: var(--color-surface-sunken);
      border: 1px solid transparent;
      border-radius: var(--radius-md);
    }
    .cc-input::placeholder { color: var(--color-text-muted); }
    .cc-input:focus-visible { border-color: var(--color-accent); outline-offset: 0; }
    .cc-send {
      flex: none;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: var(--control-height);
      height: var(--control-height);
      border: 0;
      border-radius: var(--radius-md);
      background: var(--color-accent);
      color: var(--color-on-accent);
      cursor: pointer;
    }
    .cc-send:hover:not(:disabled) { background: var(--color-accent-hover); }
    .cc-send:disabled { opacity: 0.5; cursor: not-allowed; }
  `],
})
export class ChatComposerComponent {
  readonly text = model('');
  readonly placeholder = input('Написать семье…');
  readonly disabled = input(false);
  readonly send = output<string>();
  readonly attach = output<void>();

  protected readonly plusIcon = Plus;
  protected readonly sendIcon = ArrowRight;
  protected readonly inputId = `fh-chat-input-${Math.random().toString(36).slice(2, 8)}`;

  protected submit(): void {
    const value = this.text().trim();
    if (!value) return;
    this.send.emit(value);
    this.text.set('');
  }
}
