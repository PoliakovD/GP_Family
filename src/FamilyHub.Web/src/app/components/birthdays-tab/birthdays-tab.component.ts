import { Component, Input, OnChanges, SimpleChanges, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import type { Birthday } from '../../models/types';

@Component({
  selector: 'app-birthdays-tab',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './birthdays-tab.component.html',
})
export class BirthdaysTabComponent implements OnChanges {
  @Input() familyId!: string;

  private readonly api = inject(ApiService);

  items: Birthday[] = [];
  form = { personName: '', date: '' };
  editingId: string | null = null;
  error: string | null = null;

  async ngOnChanges(changes: SimpleChanges): Promise<void> {
    if (changes['familyId']) {
      this.resetForm();
      await this.refresh();
    }
  }

  async refresh(): Promise<void> {
    try {
      this.items = await this.api.getBirthdays(this.familyId);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить дни рождения.';
    }
  }

  async handleSubmit(): Promise<void> {
    if (!this.form.personName.trim() || !this.form.date) return;
    const input = { personName: this.form.personName.trim(), date: this.form.date };
    try {
      if (this.editingId) {
        await this.api.updateBirthday(this.editingId, input);
      } else {
        await this.api.createBirthday(this.familyId, input);
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
