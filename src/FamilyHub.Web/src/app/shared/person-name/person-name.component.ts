import { Component, computed, inject, input } from '@angular/core';
import { BreakpointService } from '../../services/breakpoint.service';
import { formatPersonName, personNameStyleFor, type PersonNameParts, type PersonNameStyle } from '../util/person-name';

/**
 * Отображение ФИО, адаптивное под ширину экрана (identity rework) — десктоп: полное ФИО,
 * планшет: Фамилия Имя И., телефон: Фамилия И.И. Всегда ставит `[title]` с полным ФИО (доступно
 * по hover/долгому тапу, даже когда видимый текст схлопнут). `styleOverride` — для мест, где
 * стиль не должен зависеть от ширины экрана (например, «Профиль» в настройках — всегда Full,
 * это же имя пользователя видит только он сам).
 */
@Component({
  selector: 'app-person-name',
  standalone: true,
  template: `<span [title]="fullName()">{{ displayName() }}</span>`,
})
export class PersonNameComponent {
  private readonly breakpoints = inject(BreakpointService);

  readonly person = input.required<PersonNameParts>();
  readonly styleOverride = input<PersonNameStyle | null>(null);

  readonly fullName = computed(() => formatPersonName(this.person(), 'full'));

  readonly displayName = computed(() => {
    const style = this.styleOverride() ?? personNameStyleFor(this.breakpoints.tier());
    return formatPersonName(this.person(), style);
  });
}
