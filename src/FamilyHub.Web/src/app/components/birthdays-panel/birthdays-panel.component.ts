import { Component, OnInit, effect, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import type { Birthday } from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';

@Component({
  selector: 'app-birthdays-panel',
  standalone: true,
  imports: [FormsModule, LoadingSpinnerComponent],
  templateUrl: './birthdays-panel.component.html',
})
export class BirthdaysPanelComponent implements OnInit {
  readonly familyId = input.required<string>();

  private readonly api = inject(ApiService);

  items: Birthday[] = [];
  form = { personName: '', date: '' };
  editingId: string | null = null;
  error: string | null = null;
  loading = true;

  private readonly MONTHS_NOM = [
    'Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь',
    'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь',
  ];
  private readonly MONTHS_GEN = [
    'января', 'февраля', 'марта', 'апреля', 'мая', 'июня',
    'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря',
  ];

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

  /** "yyyy-MM-dd" -> Date в локальной полуночи (без сдвига часовым поясом, в отличие от `new Date(str)`). */
  private parseLocalDate(dateStr: string): Date {
    const [y, m, d] = dateStr.split('-').map(Number);
    return new Date(y, m - 1, d);
  }

  /** Сколько дней до ближайшего наступления дня рождения (0 — сегодня, считая от локальной полуночи). */
  private daysUntilNext(item: Birthday): number {
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const bday = this.parseLocalDate(item.date);
    let next = new Date(today.getFullYear(), bday.getMonth(), bday.getDate());
    if (next.getTime() < today.getTime()) {
      next = new Date(today.getFullYear() + 1, bday.getMonth(), bday.getDate());
    }
    return Math.round((next.getTime() - today.getTime()) / 86_400_000);
  }

  /** Возраст, который исполнится в ближайшее наступление дня рождения. */
  private nextAge(item: Birthday): number {
    const bday = this.parseLocalDate(item.date);
    const daysUntil = this.daysUntilNext(item);
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const nextOccurrence = new Date(today.getTime() + daysUntil * 86_400_000);
    return nextOccurrence.getFullYear() - bday.getFullYear();
  }

  /** Имя + акцент на срочности для ближайших дней рождения (как в дизайн-дэке). */
  urgencyLabel(item: Birthday): string {
    const days = this.daysUntilNext(item);
    if (days === 0) return `${item.personName} — сегодня!`;
    if (days === 1) return `${item.personName} — уже завтра!`;
    return item.personName;
  }

  metaLabel(item: Birthday): string {
    const bday = this.parseLocalDate(item.date);
    return `${bday.getDate()} ${this.MONTHS_GEN[bday.getMonth()]} · исполняется ${this.nextAge(item)}`;
  }

  /** Список сгруппирован по месяцу дня рождения, начиная с текущего месяца (по кругу). */
  get groupedByMonth(): { month: string; items: Birthday[] }[] {
    const groups = new Map<number, Birthday[]>();
    for (const item of this.items) {
      const m = this.parseLocalDate(item.date).getMonth();
      const list = groups.get(m);
      if (list) list.push(item);
      else groups.set(m, [item]);
    }

    const currentMonth = new Date().getMonth();
    return [...groups.entries()]
      .sort(([a], [b]) => (a - currentMonth + 12) % 12 - ((b - currentMonth + 12) % 12))
      .map(([m, items]) => ({
        month: this.MONTHS_NOM[m],
        items: items
          .slice()
          .sort((a, b) => this.parseLocalDate(a.date).getDate() - this.parseLocalDate(b.date).getDate()),
      }));
  }
}
