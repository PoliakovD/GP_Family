import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';

/**
 * Переиспользуемое поле поиска (лупа + инпут + кнопка «очистить») — единый вид на Главной и
 * внутри разделов (Аптечка/Анализы/Дни рождения). Вёрстка перенесена из исходного
 * SearchComponent, который эту роль раньше выполнял в одиночку.
 *
 * Управляется классическим `@Input()/@Output()` (не `model()`), как остальные компоненты в
 * проекте (см. `MedicationsPanelComponent.countChanged`) — `[value]`+`(valueChange)="..."`
 * банан-в-коробке работает как обычное двустороннее связывание, но не запускает debounce/поиск
 * сам по себе: это осознанно, вызывающий код сам решает, что делать по изменению (см.
 * DebouncedSearch.onQueryChange() / локальный matchesQuery()).
 */
@Component({
  selector: 'app-search-field',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './search-field.component.html',
})
export class SearchFieldComponent {
  @Input() placeholder = 'Поиск…';
  @Input() value = '';
  @Output() readonly valueChange = new EventEmitter<string>();

  onInput(value: string): void {
    this.value = value;
    this.valueChange.emit(value);
  }

  clear(): void {
    this.onInput('');
  }
}
