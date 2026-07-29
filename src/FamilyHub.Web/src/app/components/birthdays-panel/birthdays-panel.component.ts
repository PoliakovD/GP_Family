import { Component, OnInit, effect, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import type { Birthday } from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';
import { matchesQuery } from '../../shared/util/local-filter';
import {
  MONTHS_NOM,
  birthdayMetaLabel,
  birthdayUrgencyLabel,
  parseLocalBirthDate,
} from '../../shared/util/birthday-date';

@Component({
  selector: 'app-birthdays-panel',
  standalone: true,
  imports: [FormsModule, LoadingSpinnerComponent, SearchFieldComponent],
  templateUrl: './birthdays-panel.component.html',
})
export class BirthdaysPanelComponent implements OnInit {
  readonly familyId = input.required<string>();

  private readonly api = inject(ApiService);

  items: Birthday[] = [];
  /** Локальный фильтр по имени — источника в SearchService для дней рождения нет (ADR-0003). */
  searchQuery = '';
  form = { personName: '', date: '' };
  editingId: string | null = null;
  error: string | null = null;
  loading = true;

  // undefined — ещё ни разу не загружали.
  private loadedFamilyId: string | undefined = undefined;

  constructor() {
    // Реагирует на смену семьи, пока панель открыта. Первичная загрузка при монтировании —
    // в ngOnInit (effect() выполняется только на следующем цикле change detection и может не
    // успеть отработать).
    effect(() => {
      const id = this.familyId();
      if (id === this.loadedFamilyId) return;
      this.resetForm();
      this.searchQuery = '';
      void this.refresh();
    });
  }

  ngOnInit(): void {
    if (this.familyId() !== this.loadedFamilyId) {
      void this.refresh();
    }
  }

  async refresh(): Promise<void> {
    const id = this.familyId();
    this.loadedFamilyId = id;
    this.loading = true;
    try {
      this.items = await this.api.getBirthdays(id);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить дни рождения.';
    } finally {
      this.loading = false;
    }
  }

  async handleSubmit(): Promise<void> {
    if (!this.form.personName.trim() || !this.form.date) return;
    const payload = { personName: this.form.personName.trim(), date: this.form.date };
    try {
      if (this.editingId) {
        await this.api.updateBirthday(this.editingId, payload);
      } else {
        await this.api.createBirthday(this.familyId(), payload);
      }
      this.resetForm();
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось сохранить запись.';
    }
  }

  startEdit(item: Birthday): void {
    this.editingId = item.id;
    this.form = { personName: item.personName, date: item.date };
  }

  async handleDelete(id: string): Promise<void> {
    try {
      await this.api.deleteBirthday(id);
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось удалить запись.';
    }
  }

  resetForm(): void {
    this.form = { personName: '', date: '' };
    this.editingId = null;
  }

  // --- Группировка по месяцу и "человеческие" подписи (кикер месяца, "уже завтра!" и т.п.) ---
  // Датовая арифметика — в shared/util/birthday-date.ts (переиспользуется BirthdayWidgetComponent).

  urgencyLabel(item: Birthday): string {
    return birthdayUrgencyLabel(item.personName, item.date);
  }

  metaLabel(item: Birthday): string {
    return birthdayMetaLabel(item.date);
  }

  /** Список, отфильтрованный по имени, — группировка ниже строится уже поверх него. */
  get filteredItems(): Birthday[] {
    return this.items.filter((item) => matchesQuery(this.searchQuery, item.personName));
  }

  /** Список сгруппирован по месяцу дня рождения, начиная с текущего месяца (по кругу). */
  get groupedByMonth(): { month: string; items: Birthday[] }[] {
    const groups = new Map<number, Birthday[]>();
    for (const item of this.filteredItems) {
      const m = parseLocalBirthDate(item.date).getMonth();
      const list = groups.get(m);
      if (list) list.push(item);
      else groups.set(m, [item]);
    }

    const currentMonth = new Date().getMonth();
    return [...groups.entries()]
      .sort(([a], [b]) => (a - currentMonth + 12) % 12 - ((b - currentMonth + 12) % 12))
      .map(([m, items]) => ({
        month: MONTHS_NOM[m],
        items: items
          .slice()
          .sort((a, b) => parseLocalBirthDate(a.date).getDate() - parseLocalBirthDate(b.date).getDate()),
      }));
  }
}
