import { Component, computed, input, ChangeDetectionStrategy } from '@angular/core';

/** Минимум данных для отрисовки — редизайн v2. `key` — стабильный идентификатор человека
 * (userId/dependentId), а не имя: цвет выбирается хэшем по нему, чтобы не менялся при
 * переименовании и не совпадал случайно у тёзок. */
export interface AvatarPerson {
  key: string;
  firstName?: string | null;
  lastName?: string | null;
}

/** 6 пар фон/текст из уже существующих тональных рамп styles.scss — новых цветов не вводится.
 * Цвет — детерминированный хэш ключа, НЕ пол: `User.Gender` nullable (заполнен не у всех), а
 * в ТЗ редизайна нет явного требования кодировать им пол на аватаре. */
// DESIGN §2.1: остальные участники — циклически из мягких фонов с соответствующим тёмным текстом.
const PALETTE: readonly (readonly [string, string])[] = [
  ['var(--color-avatar-warm)', 'var(--color-avatar-warm-text)'],
  ['var(--color-accent-soft)', 'var(--color-accent-hover)'],
  ['var(--color-kind-sleep-soft)', 'var(--color-kind-sleep)'],
  ['var(--color-success-soft)', 'var(--color-success)'],
];
// Свой аватар — акцентный.
const SELF: readonly [string, string] = ['var(--color-accent)', 'var(--color-on-accent)'];

/** Простой строковый хэш (не криптографический) — достаточно для равномерного распределения
 * по 6 цветам палитры. */
function hashString(value: string): number {
  let hash = 0;
  for (let i = 0; i < value.length; i++) {
    hash = (hash * 31 + value.charCodeAt(i)) | 0;
  }
  return Math.abs(hash);
}

/**
 * Аватар с инициалами — редизайн v2, первый такой компонент в проекте (аватаров/фото профиля
 * нигде не было). Используется на Главной («Кто в семье»), в группах «Анализов» по человеку,
 * в карточках участников «Семьи» и в шапке записи на экране показателей.
 */
@Component({
  selector: 'app-avatar',
  standalone: true,
  template: `
    <span
      class="avatar"
      [class]="'avatar-' + size()"
      [style.background]="colors()[0]"
      [style.color]="colors()[1]"
    >{{ initials() }}</span>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './avatar.component.scss',
})
export class AvatarComponent {
  readonly person = input.required<AvatarPerson>();
  /** xs 24 · sm 32 (чат) · md 40 · lg 44 (person-row) · xl 56 (шапка семьи). */
  readonly size = input<'xs' | 'sm' | 'md' | 'lg' | 'xl'>('md');
  /** Свой аватар — акцентный (DESIGN §2.1). */
  readonly self = input(false);

  readonly initials = computed(() => {
    const { firstName, lastName } = this.person();
    const first = firstName?.trim()?.charAt(0) ?? '';
    const last = lastName?.trim()?.charAt(0) ?? '';
    const combined = (first + last).toUpperCase();
    return combined || '?';
  });

  readonly colors = computed(() => (this.self() ? SELF : PALETTE[hashString(this.person().key) % PALETTE.length]));
}
