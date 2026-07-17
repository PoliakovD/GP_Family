import { Component, OnInit, inject, effect } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import type { Birthday } from '../../models/types';

@Component({
  selector: 'app-birthdays-tab',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './birthdays-tab.component.html',
})
export class BirthdaysTabComponent implements OnInit {
  readonly state = inject(FamilyStateService);
  private readonly api = inject(ApiService);

  items: Birthday[] = [];
  form = { personName: '', date: '' };
  editingId: string | null = null;
  error: string | null = null;

  // undefined — ещё ни разу не загружали; отличаем от id === null (семьи нет).
  private loadedFamilyId: string | null | undefined = undefined;

  constructor() {
    // Реагирует на переключение активной семьи, пока вкладка открыта.
    // Первичная загрузка при монтировании — в ngOnInit (effect() выполняется
    // только на следующем цикле change detection и может не успеть отработать).
    effect(() => {
      const id = this.state.activeFamilyId();
      if (id === this.loadedFamilyId) return;
      this.resetForm();
      if (id) {
        void this.refresh();
      } else {
        this.loadedFamilyId = null;
        this.items = [];
      }
    });
  }

  ngOnInit(): void {
    const id = this.state.activeFamilyId();
    if (id && id !== this.loadedFamilyId) {
      void this.refresh();
    }
  }

  async refresh(): Promise<void> {
    const id = this.state.activeFamilyId();
    if (!id) return;
    this.loadedFamilyId = id;
    try {
      this.items = await this.api.getBirthdays(id);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить дни рождения.';
    }
  }

  async handleSubmit(): Promise<void> {
    const id = this.state.activeFamilyId();
    if (!id || !this.form.personName.trim() || !this.form.date) return;
    const input = { personName: this.form.personName.trim(), date: this.form.date };
    try {
      if (this.editingId) {
        await this.api.updateBirthday(this.editingId, input);
      } else {
        await this.api.createBirthday(id, input);
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
}
