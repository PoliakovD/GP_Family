import { Component, OnInit, effect, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { BirthdaySource, type Birthday } from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';
import { matchesQuery } from '../../shared/util/local-filter';
import {
  MONTHS_NOM,
  birthdayMetaLabel,
  birthdayUrgencyLabel,
  parseLocalBirthDate,
} from '../../shared/util/birthday-date';

let nextInstanceId = 0;

@Component({
    selector: 'app-birthdays-panel',
    imports: [FormsModule, LoadingSpinnerComponent, SearchFieldComponent],
    templateUrl: './birthdays-panel.component.html'
})
export class BirthdaysPanelComponent implements OnInit {
  readonly familyId = input.required<string>();

  private readonly api = inject(ApiService);
  private readonly confirm = inject(ConfirmService);

  /** Уникальные id полей — панель может быть смонтирована в нескольких местах. */
  readonly fieldId = `birthday-${nextInstanceId++}`;
  /** Защита от двойного тапа «Добавить» — раньше второй тап создавал дубль. */
  saving = false;

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
    if (this.saving) return;
    if (!this.form.personName.trim() || !this.form.date) {
      this.error = !this.form.personName.trim() ? 'Укажите имя.' : 'Укажите дату рождения.';
      return;
    }
    const payload = { personName: this.form.personName.trim(), date: this.form.date };
    this.saving = true;
    try {
      if (this.editingId) {
        await this.api.updateBirthday(this.editingId, payload);
      } else {
        await this.api.createBirthday(this.familyId(), payload);
      }
      this.error = null;
      this.resetForm();
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось сохранить запись.';
    } finally {
      this.saving = false;
    }
  }

  startEdit(item: Birthday): void {
    this.editingId = item.id;
    this.form = { personName: item.personName, date: item.date };
  }

  async handleDelete(item: Birthday): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Удалить день рождения?',
      message: `День рождения «${item.personName}» будет удалён. Это действие нельзя отменить.`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;
    try {
      await this.api.deleteBirthday(item.id);
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

  /** Только ручные записи (Birthday) редактируемы — участники/подопечные производные из
   * профиля User/FamilyDependent, правятся там (в настройках/на панели «Близкие и питомцы»). */
  isManual(item: Birthday): boolean {
    return item.source === BirthdaySource.Manual;
  }

  sourceLabel(item: Birthday): string | null {
    switch (item.source) {
      case BirthdaySource.Member: return 'участник семьи';
      case BirthdaySource.Dependent: return 'подопечный';
      default: return null;
    }
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
