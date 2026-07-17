import { Component, OnInit, inject, effect } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import type { Medication } from '../../models/types';

@Component({
  selector: 'app-medications-tab',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './medications-tab.component.html',
})
export class MedicationsTabComponent implements OnInit {
  readonly state = inject(FamilyStateService);
  private readonly api = inject(ApiService);

  items: Medication[] = [];
  form = { name: '', instructions: '', expiryDate: '', quantity: 1 };
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
      this.items = await this.api.getMedications(id);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить аптечку.';
    }
  }

  async handleSubmit(): Promise<void> {
    const id = this.state.activeFamilyId();
    if (!id || !this.form.name.trim()) return;
    const input = {
      name: this.form.name.trim(),
      instructions: this.form.instructions.trim() || null,
      expiryDate: this.form.expiryDate || null,
      quantity: Number(this.form.quantity) || 0,
    };
    try {
      if (this.editingId) {
        await this.api.updateMedication(this.editingId, input);
      } else {
        await this.api.createMedication(id, input);
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
