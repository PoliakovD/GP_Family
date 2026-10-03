import { Component, OnInit, effect, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import { todayLocal } from '../../shared/util/intake-labels';
import { formatDayMonthYear } from '../../shared/util/date-format';
import { FamilyStateService } from '../../services/family-state.service';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { FamilyRole, Gender } from '../../models/types';
import type { FamilyDependent } from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { PersonNameComponent } from '../../shared/person-name/person-name.component';

/**
 * Panel «Близкие и питомцы» — семейный ресурс (дети/питомцы/пожилые родственники без своего
 * User): та же структура, что birthdays-panel (инлайн-форма + плоский список), плюс чекбокс
 * "Это питомец", раскрывающий поле "Вид животного". Create/Update — любой активный участник;
 * Delete — только Admin (сервер перепроверит роль, здесь только прячем кнопку).
 */
let nextInstanceId = 0;

@Component({
    selector: 'app-dependents-panel',
    imports: [FormsModule, LoadingSpinnerComponent, PersonNameComponent],
    templateUrl: './dependents-panel.component.html'
})
export class DependentsPanelComponent implements OnInit {
  readonly familyId = input.required<string>();
  readonly Gender = Gender;

  private readonly api = inject(ApiService);
  private readonly state = inject(FamilyStateService);
  private readonly confirm = inject(ConfirmService);

  readonly fieldId = `dependent-${nextInstanceId++}`;
  readonly today = todayLocal();
  readonly formatDayMonthYear = formatDayMonthYear;
  /** Защита от двойного тапа «Добавить» — раньше второй тап создавал дубль. */
  saving = false;

  items: FamilyDependent[] = [];
  // gender: null — не выбран (раньше по умолчанию «Мужской» → неверные нормы анализов у девочек).
  form = {
    firstName: '', lastName: '', middleName: '', gender: null as number | null,
    birthDate: '', isPet: false, petSpecies: '',
  };
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
    if (this.saving) return;
    // Раньше пустые поля молча ничего не делали — теперь говорим, чего не хватает.
    if (!this.form.isPet && !this.form.lastName.trim()) { this.error = 'Укажите фамилию.'; return; }
    if (!this.form.firstName.trim()) { this.error = this.form.isPet ? 'Укажите кличку.' : 'Укажите имя.'; return; }
    if (this.form.gender === null) { this.error = 'Выберите пол.'; return; }
    const gender: number = this.form.gender;
    const payload = {
      firstName: this.form.firstName.trim(),
      lastName: this.form.isPet ? null : this.form.lastName.trim() || null,
      middleName: this.form.isPet ? null : this.form.middleName.trim() || null,
      gender,
      birthDate: this.form.birthDate || null,
      isPet: this.form.isPet,
      petSpecies: this.form.isPet ? this.form.petSpecies.trim() || null : null,
    };
    this.saving = true;
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
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось сохранить запись.';
    } finally {
      this.saving = false;
    }
  }

  startEdit(item: FamilyDependent): void {
    this.editingId = item.id;
    this.form = {
      firstName: item.firstName,
      lastName: item.lastName ?? '',
      middleName: item.middleName ?? '',
      gender: item.gender,
      birthDate: item.birthDate ?? '',
      isPet: item.isPet,
      petSpecies: item.petSpecies ?? '',
    };
    // Форма — вверху панели: без прокрутки на длинном списке казалось, что «Изменить» не сработало.
    queueMicrotask(() => {
      const el = document.getElementById(this.fieldId + (item.isPet ? '-first' : '-last'));
      el?.scrollIntoView({ behavior: 'smooth', block: 'center' });
      el?.focus({ preventScroll: true });
    });
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
    this.form = {
      firstName: '', lastName: '', middleName: '', gender: null,
      birthDate: '', isPet: false, petSpecies: '',
    };
    this.editingId = null;
  }
}
