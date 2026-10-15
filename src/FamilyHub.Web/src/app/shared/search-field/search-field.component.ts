import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Search, X } from 'lucide-angular';
import { IconComponent } from '../ui/icon/icon.component';

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
    imports: [FormsModule, IconComponent],
    templateUrl: './search-field.component.html',
    styles: [`
      :host { display: block; min-width: 0; }
      .search-field { width: 100%; }
      .search-field .input { padding-right: var(--tap-min); }
      .search-field-clear { position: absolute; right: 1px; }
      /* Нативный крестик type="search" дублирует наш. */
      input::-webkit-search-cancel-button { display: none; }
    `],
})
export class SearchFieldComponent {
  @Input() placeholder = 'Поиск…';
  @Input() value = '';
  /** Редизайн v2 — без нижнего отступа для мест, где поле стоит само по себе в строке (топбар
   * каркаса), а не как первый элемент страницы над остальным контентом. */
  @Input() compact = false;
  @Output() readonly valueChange = new EventEmitter<string>();

  protected readonly searchIcon = Search;
  protected readonly clearIcon = X;

  onInput(value: string): void {
    this.value = value;
    this.valueChange.emit(value);
  }

  clear(): void {
    this.onInput('');
  }
}
