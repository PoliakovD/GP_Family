import { Injectable, signal } from '@angular/core';

export type BreakpointTier = 'wide' | 'medium' | 'narrow';

/**
 * Первая брейкпойнт-абстракция в проекте (identity rework) — до этого во всём styles.scss был
 * единственный `@media (max-width: 640px)`, специфичный для экрана логина, и никакого
 * BreakpointObserver (@angular/cdk не подключён). Пороги — под адаптивное отображение ФИО
 * (person-name.ts/person-name.component.ts): 'wide' (десктоп, ≥1024px) — полное ФИО,
 * 'medium' (планшет, 640–1023px) — Фамилия Имя И., 'narrow' (телефон, <640px) — Фамилия И.И.
 */
@Injectable({ providedIn: 'root' })
export class BreakpointService {
  readonly tier = signal<BreakpointTier>(this.computeTier());

  constructor() {
    const wideQuery = matchMedia('(min-width: 1024px)');
    const mediumQuery = matchMedia('(min-width: 640px)');
    const update = () => this.tier.set(this.computeTier());
    wideQuery.addEventListener('change', update);
    mediumQuery.addEventListener('change', update);
  }

  private computeTier(): BreakpointTier {
    if (matchMedia('(min-width: 1024px)').matches) return 'wide';
    if (matchMedia('(min-width: 640px)').matches) return 'medium';
    return 'narrow';
  }
}
