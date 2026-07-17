import { Component, OnInit, effect, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import type { Medication } from '../../models/types';

@Component({
  selector: 'app-medications-panel',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './medications-panel.component.html',
})
export class MedicationsPanelComponent implements OnInit {
  readonly medkitId = input.required<string>();

  private readonly api = inject(ApiService);

  items: Medication[] = [];
  form = { name: '', instructions: '', expiryDate: '', quantity: 1 };
  editingId: string | null = null;
  error: string | null = null;

  // undefined — ещё ни разу не загружали.
  private loadedMedkitId: string | undefined = undefined;

  constructor() {
    // Реагирует на смену аптечки (например, при раскрытии другой карточки), пока панель открыта.
    // Первичная загрузка при монтировании — в ngOnInit (effect() выполняется только на
    // следующем цикле change detection и может не успеть отработать).
    effect(() => {
      const id = this.medkitId();
      if (id === this.loadedMedkitId) return;
      this.resetForm();
      void this.refresh();
    });
  }

  ngOnInit(): void {
    if (this.medkitId() !== this.loadedMedkitId) {
      void this.refresh();
    }
  }

  async refresh(): Promise<void> {
    const id = this.medkitId();
    this.loadedMedkitId = id;
    try {
      this.items = await this.api.getMedications(id);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить аптечку.';
    }
  }

  async handleSubmit(): Promise<void> {
    if (!this.form.name.trim()) return;
    const payload = {
      name: this.form.name.trim(),
      instructions: this.form.instructions.trim() || null,
      expiryDate: this.form.expiryDate || null,
      quantity: Number(this.form.quantity) || 0,
    };
    try {
      if (this.editingId) {
        await this.api.updateMedication(this.editingId, payload);
      } else {
        await this.api.createMedication(this.medkitId(), payload);
      }
      this.resetForm();
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось сохранить запись.';
    }
  }

  startEdit(item: Medication): void {
    this.editingId = item.id;
    this.form = {
      name: item.name,
      instructions: item.instructions ?? '',
      expiryDate: item.expiryDate ?? '',
      quantity: item.quantity,
    };
  }

  async handleDelete(id: string): Promise<void> {
    try {
      await this.api.deleteMedication(id);
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось удалить запись.';
    }
  }

  resetForm(): void {
    this.form = { name: '', instructions: '', expiryDate: '', quantity: 1 };
    this.editingId = null;
  }
}
