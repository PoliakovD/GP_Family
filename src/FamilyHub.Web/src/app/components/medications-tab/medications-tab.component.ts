import { Component, Input, OnChanges, SimpleChanges, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import type { Medication } from '../../models/types';

@Component({
  selector: 'app-medications-tab',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './medications-tab.component.html',
})
export class MedicationsTabComponent implements OnChanges {
  @Input() familyId!: string;

  private readonly api = inject(ApiService);

  items: Medication[] = [];
  form = { name: '', instructions: '', expiryDate: '', quantity: 1 };
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
      this.items = await this.api.getMedications(this.familyId);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить аптечку.';
    }
  }

  async handleSubmit(): Promise<void> {
    if (!this.form.name.trim()) return;
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
        await this.api.createMedication(this.familyId, input);
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
