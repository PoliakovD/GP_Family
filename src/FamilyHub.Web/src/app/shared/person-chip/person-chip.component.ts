import { Component, input } from '@angular/core';
import { AvatarComponent, type AvatarPerson } from '../avatar/avatar.component';

/**
 * Редизайн v2.1 — компактное представление человека там, где раньше стояло полное ФИО (иногда
 * с именем семьи в скобках): аватар с двумя буквами инициалов + короткое имя ("Фамилия И.И."),
 * всё в прямоугольнике; полное имя доступно по наведению через нативный [title]. Замечание из
 * заметок: "список пользователей должен быть покороче" — применяется на фильтрах "Чей анализ"
 * (Анализы/Врачи/Показатели/добавление записи) и годится для любого другого места со списком
 * людей.
 *
 * Только отображение — компонент не кликабельный сам по себе: там, где нужна интерактивность
 * (фильтр-чип), вызывающая сторона оборачивает его в свой `<button class="person-chip-btn">`
 * (см. .person-chip-btn ниже) и сама решает, что делает клик; `selected` только красит рамку.
 */
@Component({
  selector: 'app-person-chip',
  standalone: true,
  imports: [AvatarComponent],
  template: `
    <span class="person-chip" [class.person-chip-selected]="selected()" [title]="fullLabel()">
      <app-avatar [person]="avatarPerson()" size="sm" />
      <span class="person-chip-label">{{ shortLabel() }}</span>
    </span>
  `,
  styleUrl: './person-chip.component.scss',
})
export class PersonChipComponent {
  readonly avatarPerson = input.required<AvatarPerson>();
  /** Короткая форма — "Фамилия И.И." (formatPersonName(..., 'initials')) для структурного ФИО,
   * shortenDisplayName(rawName) там, где с бэка приходит уже готовая строка (см.
   * MedicalRecordService.ResolvePersonNamesAsync), либо кличка питомца как есть. */
  readonly shortLabel = input.required<string>();
  /** Полное ФИО (+ семья, если применимо) — только в title-тултип, не в тексте чипа. */
  readonly fullLabel = input.required<string>();
  readonly selected = input(false);
}
