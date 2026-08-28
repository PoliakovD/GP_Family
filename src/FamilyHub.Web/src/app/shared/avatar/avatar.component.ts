import { Component, computed, input } from '@angular/core';

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
const PALETTE: readonly (readonly [string, string])[] = [
  ['var(--color-accent-200)', 'var(--color-accent-800)'],
  ['var(--color-accent-2-200)', 'var(--color-accent-2-800)'],
  ['var(--color-neutral-200)', 'var(--color-neutral-800)'],
  ['var(--color-accent-100)', 'var(--color-accent-700)'],
  ['var(--color-accent-2-100)', 'var(--color-accent-2-700)'],
  ['var(--color-neutral-100)', 'var(--color-neutral-700)'],
];

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
  styleUrl: './avatar.component.scss',
})
export class AvatarComponent {
  readonly person = input.required<AvatarPerson>();
  readonly size = input<'sm' | 'md' | 'lg'>('md');

  readonly initials = computed(() => {
    const { firstName, lastName } = this.person();
    const first = firstName?.trim()?.charAt(0) ?? '';
    const last = lastName?.trim()?.charAt(0) ?? '';
    const combined = (first + last).toUpperCase();
    return combined || '?';
  });

  readonly colors = computed(() => PALETTE[hashString(this.person().key) % PALETTE.length]);
}
