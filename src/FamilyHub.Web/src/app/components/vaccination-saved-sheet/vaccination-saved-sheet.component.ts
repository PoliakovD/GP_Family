import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { ApiError, ApiService } from '../../services/api.service';
import { ToastService } from '../../shared/toast/toast.service';
import { HealthNoteCatalog, HealthNoteKind, VaccinationDatePrecision, VaccinationKind } from '../../models/types';
import { HealthNoteFormComponent } from '../health-note-form/health-note-form.component';
import { VaccinationStateService, type VaccinationSavedInfo } from '../../services/vaccination-state.service';
import { todayLocal } from '../../shared/util/intake-labels';
import { formatDayMonthYear } from '../../shared/util/date-format';

type QuickKind = 'symptom' | 'temperature';

/**
 * Шторка «Прививка сохранена» (макет «Screen - Vaccination»): подсказка, когда ждать реакцию, и
 * (только для своих прививок — дневник строго личный, решение сентября 2026) быстрые записи
 * температуры/симптома + переключатель «напомнить о самочувствии через 7 дней». Для подопечных —
 * только подсказка, без дневника и без переключателя.
 */
@Component({
  selector: 'app-vaccination-saved-sheet',
  imports: [HealthNoteFormComponent],
  templateUrl: './vaccination-saved-sheet.component.html',
  styleUrl: './vaccination-saved-sheet.component.scss',
})
export class VaccinationSavedSheetComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly vaccinations = inject(VaccinationStateService);

  readonly info = input.required<VaccinationSavedInfo>();
  readonly closed = output<void>();

  protected readonly Kind = HealthNoteKind;
  readonly quickKind = signal<QuickKind | null>(null);
  readonly catalog = signal<HealthNoteCatalog | null>(null);
  // Все места создания прививки шлют requestWellbeingCheck: false — значит, напоминание выключено.
  // Раньше переключатель стартовал включённым, и человек ждал напоминания, которое не придёт.
  readonly reminderOn = signal(false);
  readonly reminderBusy = signal(false);

  /** «Отметить сделанной» ставит сегодняшнюю дату — если прививку делали раньше, правим здесь же,
   * иначе неверная дата сдвигала график следующих доз. */
  readonly editingDate = signal(false);
  readonly dateBusy = signal(false);
  readonly savedDate = signal<string | null>(null);
  protected readonly today = todayLocal();

  protected async saveDate(date: string): Promise<void> {
    const recordId = this.info().item.recordId;
    if (!recordId || !date || this.dateBusy()) return;
    if (date > this.today) {
      this.toast.error('Дата прививки не может быть в будущем.');
      return;
    }
    this.dateBusy.set(true);
    try {
      await this.api.updateVaccination(recordId, {
        vaccineName: null,
        kind: VaccinationKind.Done,
        date,
        datePrecision: VaccinationDatePrecision.Day,
      });
      this.savedDate.set(date);
      this.editingDate.set(false);
      this.vaccinations.changed();
      this.toast.success('Дата прививки изменена.');
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось изменить дату.');
    } finally {
      this.dateBusy.set(false);
    }
  }

  /** «сегодня» — только если прививка и правда сегодняшняя; иначе реальная дата (раньше всегда
   * писалось «сегодня», даже для прошлой даты — выглядело, будто дата не сохранилась). */
  protected dateText(): string {
    const date = this.savedDate() ?? this.info().item.date;
    if (!date || date === todayLocal()) return 'сегодня';
    return formatDayMonthYear(date);
  }

  ngOnInit(): void {
    if (this.info().subject.isSelf) void this.loadCatalog();
  }

  protected openQuick(kind: QuickKind): void {
    this.quickKind.set(kind);
  }

  protected onQuickSaved(): void {
    this.quickKind.set(null);
    this.toast.success('Записано в дневник.');
  }

  protected async toggleReminder(checked: boolean): Promise<void> {
    const recordId = this.info().item.recordId;
    if (!recordId) return;
    this.reminderOn.set(checked);
    this.reminderBusy.set(true);
    try {
      await this.api.setVaccinationWellbeingCheck(recordId, checked);
    } catch (e) {
      this.reminderOn.set(!checked);
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось изменить напоминание.');
    } finally {
      this.reminderBusy.set(false);
    }
  }

  private async loadCatalog(): Promise<void> {
    try {
      this.catalog.set(await this.api.getHealthNoteCatalog());
    } catch {
      // Быстрые формы работают и без каталога (единицы измерения возьмутся из дефолта поля).
    }
  }
}
