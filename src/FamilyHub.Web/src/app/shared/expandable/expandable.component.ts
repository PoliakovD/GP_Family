import { Component, EventEmitter, Input, Output, ChangeDetectionStrategy } from '@angular/core';
import { ChevronDown } from 'lucide';
import { IconComponent } from '../ui/icon/icon.component';

/**
 * Раскрывающийся блок (UX-редизайн) — вынесен из проверенной техники `medkit-card`
 * (`components/medkits-panel/medkits-panel.component.scss`): grid `0fr → 1fr` +
 * `grid-template-columns: minmax(0, 1fr)` (без него неявная колонка растёт по max-content
 * содержимого и раздвигает viewport) + поворот шеврона на 180°.
 *
 * `@Input()/@Output()` (не `model()`), как и `app-search-field` — вызывающий код сам решает, что
 * делать по раскрытию (например, лениво загрузить содержимое), а не просто держит булев флаг.
 * Управляется классическим two-way `[open]="x" (openChange)="x = $event"`.
 */
@Component({
  selector: 'app-expandable',
  imports: [IconComponent],
  standalone: true,
  templateUrl: './expandable.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './expandable.component.scss',
})
export class ExpandableComponent {
  protected readonly icons = { ChevronDown };

  @Input() title = '';
  /** Необязательный счётчик рядом с заголовком, напр. «Файлы (3)» — передавать null, чтобы скрыть. */
  @Input() count: number | null = null;
  @Input() open = false;
  @Output() readonly openChange = new EventEmitter<boolean>();

  toggle(): void {
    this.open = !this.open;
    this.openChange.emit(this.open);
  }
}
