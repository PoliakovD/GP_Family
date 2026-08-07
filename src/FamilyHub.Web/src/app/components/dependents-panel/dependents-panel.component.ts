import { Component, OnInit, effect, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { FamilyRole } from '../../models/types';
import type { FamilyDependent } from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';

/**
 * Panel «Близкие и питомцы» — семейный ресурс (дети/питомцы/пожилые родственники без своего
 * User): та же структура, что birthdays-panel (инлайн-форма + плоский список), плюс чекбокс
 * "Это питомец", раскрывающий поле "Вид животного". Create/Update — любой активный участник;
 * Delete — только Admin (сервер перепроверит роль, здесь только прячем кнопку).
 */
@Component({
  selector: 'app-dependents-panel',
  standalone: true,
  imports: [FormsModule, LoadingSpinnerComponent],
  templateUrl: './dependents-panel.component.html',
})
export class DependentsPanelComponent implements OnInit {
  readonly familyId = input.required<string>();

  private readonly api = inject(ApiService);
  private readonly state = inject(FamilyStateService);
  private readonly confirm = inject(ConfirmService);

  items: FamilyDependent[] = [];
  form = { name: '', birthDate: '', isPet: false, petSpecies: '' };
  editingId: string | null = null;
  error: string | null = null;
  loading = true;

  // undefined — ещё ни разу не загружали.
  private loadedFamilyId: string | undefined = undefined;

  constructor() {
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

  get isAdmin(): boolean {
    return this.state.families().find((f) => f.id === this.familyId())?.myRole === FamilyRole.Admin;
  }

  async refresh(): Promise<void> {
    const id = this.familyId();
    this.loadedFamilyId = id;
    this.loading = true;
    try {
      this.items = await this.api.getDependents(id);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить список.';
    } finally {
      this.loading = false;
    }
  }

  async handleSubmit(): Promise<void> {
    if (!this.form.name.trim()) return;
    const payload = {
      name: this.form.name.trim(),
      birthDate: this.form.birthDate || null,
      isPet: this.form.isPet,
      petSpecies: this.form.isPet ? this.form.petSpecies.trim() || null : null,
    };
    try {
      if (this.editingId) {
        await this.api.updateDependent(this.editingId, payload);
      } else {
        await this.api.createDependent(this.familyId(), payload);
      }
      this.resetForm();
      await this.refresh();
      // Дропдаун "Кто пациент?" в медзаписях читает FamilySummary.dependents из общего состояния —
      // держим его в курсе, не дожидаясь (не блокирует UI этой панели).
      void this.state.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось сохранить запись.';
    }
  }

  startEdit(item: FamilyDependent): void {
    this.editingId = item.id;
    this.form = {
      name: item.name,
      birthDate: item.birthDate ?? '',
      isPet: item.isPet,
      petSpecies: item.petSpecies ?? '',
    };
  }

  async handleDelete(id: string): Promise<void> {
    const confirmed = await this.confirm.confirm({
      title: 'Удалить профиль?',
      message: 'Профиль и все связанные с ним анализы/посещения врачей будут удалены безвозвратно.',
      confirmText: 'Удалить',
      danger: true,
    });
    if (!confirmed) return;

    try {
      await this.api.deleteDependent(id);
      await this.refresh();
      void this.state.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось удалить запись.';
    }
  }

  resetForm(): void {
    this.form = { name: '', birthDate: '', isPet: false, petSpecies: '' };
    this.editingId = null;
  }
}
