import { Component, OnInit, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, ApiService } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import { IntakeFormRequest, IntakeStateService } from '../../services/intake-state.service';
import { ToastService } from '../../shared/toast/toast.service';
import { formatDayMonth } from '../../shared/util/date-format';
import {
  CourseDetail, CoursePreview, CourseRequest, DoseSchedule, DoseScheduleMode, DoseUnit, FoodRelation,
  PrescriptionItem, PrescriptionVisit,
} from '../../models/types';
import {
  DOSE_UNITS, FOOD_OPTIONS, WEEKDAY_ORDER, coversText, daysBetween, describeSchedule, endDateFor, formatNumber,
  fromTimeInput, scheduleTimes, clock, todayLocal, toTimeInput, weekdayShort,
} from '../../shared/util/intake-labels';

interface TimeRow {
  /** «HH:mm» для <input type="time">. */
  at: string;
  units: number;
}

type DurationKind = 'days' | 'weeks' | 'months' | 'forever';

interface MedOption {
  id: string;
  name: string;
  medkitName: string;
  familyId: string;
  quantity: string | null;
}

interface SubjectOption {
  id: string | null;
  label: string;
  familyId: string | null;
}

const FREQUENCIES: { mode: DoseScheduleMode; label: string }[] = [
  { mode: DoseScheduleMode.TimesPerDay, label: 'N раз в день' },
  { mode: DoseScheduleMode.EveryNHours, label: 'Каждые N часов' },
  { mode: DoseScheduleMode.Weekdays, label: 'По дням недели' },
  { mode: DoseScheduleMode.Cycle, label: 'Через день / цикл' },
  { mode: DoseScheduleMode.AsNeeded, label: 'По необходимости' },
];

const INTERVAL_HOURS = [2, 3, 4, 6, 8, 12];
const MISSED_AFTER = [
  { value: 30, label: '30 минут' }, { value: 60, label: '1 час' }, { value: 120, label: '2 часа' },
  { value: 180, label: '3 часа' }, { value: 240, label: '4 часа' },
];
const DURATION_MULTIPLIER: Record<Exclude<DurationKind, 'forever'>, number> = { days: 1, weeks: 7, months: 30 };

/** Времена по умолчанию, когда из назначения известно только «N раз в день». */
const DEFAULT_TIMES: Record<number, string[]> = {
  1: ['08:00'], 2: ['08:00', '20:00'], 3: ['08:00', '14:00', '20:00'], 4: ['08:00', '12:00', '16:00', '20:00'],
};

/**
 * Форма нового/изменяемого курса (макет «Screen - Medication schedule», «новый курс»). Не владеет оверлеем:
 * страница-хаб кладёт её в `app-side-panel` (десктоп) или `app-bottom-sheet` (мобайл). Курс создаётся из
 * назначения врача (поля заполняются из текста назначения — «проверьте») или вручную. Меняется только блок
 * «Как часто» — остальная форма от режима не зависит; внизу расписание пересказано фразой и пересчитывается
 * при каждом изменении.
 */
@Component({
  selector: 'app-intake-course-form',
  imports: [FormsModule],
  templateUrl: './intake-course-form.component.html',
  styleUrl: './intake-course-form.component.scss',
})
export class IntakeCourseFormComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly families = inject(FamilyStateService);
  protected readonly intake = inject(IntakeStateService);

  readonly request = input<IntakeFormRequest>({});
  readonly saved = output<void>();
  readonly cancelled = output<void>();

  protected readonly frequencies = FREQUENCIES;
  protected readonly intervalHours = INTERVAL_HOURS;
  protected readonly missedOptions = MISSED_AFTER;
  protected readonly foodOptions = FOOD_OPTIONS;
  protected readonly doseUnits = DOSE_UNITS;
  protected readonly weekdayOrder = WEEKDAY_ORDER;
  protected readonly weekdayShort = weekdayShort;
  protected readonly Mode = DoseScheduleMode;
  protected readonly coversText = coversText;

  readonly editingId = signal<string | null>(null);
  readonly source = signal<'prescription' | 'manual'>('manual');
  readonly dependentId = signal<string | null>(null);

  readonly prescriptions = signal<PrescriptionVisit[]>([]);
  readonly prescriptionsLoading = signal(false);
  readonly selectedPrescription = signal<{ recordId: string; index: number } | null>(null);
  readonly sourceRecordId = signal<string | null>(null);
  readonly sourceIndex = signal<number | null>(null);
  readonly prescriptionText = signal<string | null>(null);
  /** Поля заполнены из назначения — просим проверить. */
  readonly prefilled = signal(false);

  readonly drugName = signal('');
  readonly freq = signal<DoseScheduleMode>(DoseScheduleMode.TimesPerDay);
  readonly times = signal<TimeRow[]>([{ at: '08:00', units: 1 }]);
  readonly everyHours = signal(12);
  readonly everyStart = signal('08:00');
  /** Доза одного приёма для «каждые N часов» и «по необходимости». */
  readonly singleUnits = signal(1);
  readonly weekdays = signal<number[]>([1, 3, 5]);
  readonly cycleOn = signal(21);
  readonly cycleOff = signal(7);
  readonly maxPerDay = signal(3);
  readonly food = signal<FoodRelation>(FoodRelation.Any);
  readonly unit = signal<DoseUnit>(DoseUnit.Tablet);
  readonly startDate = signal(todayLocal());
  readonly durKind = signal<DurationKind>('weeks');
  readonly durValue = signal(1);
  readonly writeOff = signal(false);
  readonly medicationId = signal<string | null>(null);
  readonly medOptions = signal<MedOption[]>([]);
  readonly medsLoaded = signal(false);
  readonly repeat = signal<number | null>(15);
  readonly missedAfter = signal(120);
  readonly lowStockDays = signal(5);
  readonly notes = signal('');

  readonly preview = signal<CoursePreview | null>(null);
  readonly busy = signal(false);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  protected readonly isEdit = computed(() => this.editingId() !== null);

  /** Кому курс: я или подопечный любой из моих семей. */
  protected readonly subjectOptions = computed<SubjectOption[]>(() => {
    const options: SubjectOption[] = [{ id: null, label: 'Для меня', familyId: null }];
    for (const family of this.families.activeFamilies()) {
      for (const dep of family.dependents ?? []) {
        options.push({ id: dep.id, label: `${dep.firstName}${dep.isPet ? ' (питомец)' : ''}`, familyId: family.id });
      }
    }
    return options;
  });

  protected readonly usesTimes = computed(() => {
    const m = this.freq();
    return m === DoseScheduleMode.TimesPerDay || m === DoseScheduleMode.Weekdays || m === DoseScheduleMode.Cycle;
  });

  protected readonly endDate = computed<string | null>(() => {
    const kind = this.durKind();
    if (kind === 'forever') return null;
    const value = Math.max(1, Math.floor(this.durValue() || 1));
    return endDateFor(this.startDate(), value * DURATION_MULTIPLIER[kind]);
  });

  protected readonly schedule = computed<DoseSchedule>(() => {
    const mode = this.freq();
    const rows = this.times().map((t) => ({ at: fromTimeInput(t.at), units: Number(t.units) }));
    switch (mode) {
      case DoseScheduleMode.EveryNHours:
        return { mode, intervalHours: this.everyHours(), intervalStart: fromTimeInput(this.everyStart()), intervalUnits: Number(this.singleUnits()) };
      case DoseScheduleMode.Weekdays:
        return { mode, times: rows, weekdays: [...this.weekdays()] };
      case DoseScheduleMode.Cycle:
        return { mode, times: rows, cycleOnDays: Number(this.cycleOn()), cycleOffDays: Number(this.cycleOff()) };
      case DoseScheduleMode.AsNeeded:
        return { mode, maxPerDay: Number(this.maxPerDay()), intervalUnits: Number(this.singleUnits()) };
      default:
        return { mode, times: rows };
    }
  });

  /** Итог расписания фразой — пересчитывается при каждом изменении. */
  protected readonly summary = computed(() => describeSchedule(this.schedule(), this.food(), this.endDate()));

  /** «→ 8:00, 20:00» под блоком «Каждые N часов». */
  protected readonly intervalTimes = computed(() =>
    this.freq() === DoseScheduleMode.EveryNHours ? scheduleTimes(this.schedule()).map((t) => clock(t.at)).join(', ') : '');

  protected readonly breakText = computed(() => {
    const d = this.preview()?.nextBreakStart;
    return this.freq() === DoseScheduleMode.Cycle && d ? `перерыв с ${formatDayMonth(d)}` : '';
  });

  protected readonly visiblePrescriptions = computed(() =>
    this.prescriptions().filter((v) => v.items.length > 0));

  protected readonly selectedItem = computed<{ visit: PrescriptionVisit; item: PrescriptionItem } | null>(() => {
    const sel = this.selectedPrescription();
    if (!sel) return null;
    const visit = this.prescriptions().find((v) => v.recordId === sel.recordId);
    const item = visit?.items.find((i) => i.index === sel.index);
    return visit && item ? { visit, item } : null;
  });

  /** Препараты аптечек: для подопечного — только его семьи, иначе всех моих. */
  protected readonly medChoices = computed(() => {
    const dep = this.subjectOptions().find((o) => o.id === this.dependentId());
    const familyId = this.dependentId() ? dep?.familyId : null;
    return this.medOptions().filter((m) => !familyId || m.familyId === familyId);
  });

  protected readonly canSave = computed(() => {
    if (this.drugName().trim() === '') return false;
    if (this.writeOff() && !this.medicationId()) return false;
    if (this.durKind() !== 'forever' && !(Number(this.durValue()) >= 1)) return false;
    if (!this.startDate()) return false;
    const rows = this.times();
    const uniqueTimes = new Set(rows.map((r) => r.at));
    const rowsOk = rows.length > 0 && rows.every((r) => !!r.at && Number(r.units) > 0) && uniqueTimes.size === rows.length;
    switch (this.freq()) {
      case DoseScheduleMode.TimesPerDay: return rowsOk;
      case DoseScheduleMode.EveryNHours: return !!this.everyStart() && Number(this.singleUnits()) > 0;
      case DoseScheduleMode.Weekdays: return rowsOk && this.weekdays().length > 0;
      case DoseScheduleMode.Cycle: return rowsOk && Number(this.cycleOn()) >= 1 && Number(this.cycleOff()) >= 1;
      default: return Number(this.maxPerDay()) >= 1 && Number(this.singleUnits()) > 0;
    }
  });

  constructor() {
    // Расчёт «на курс нужно N, в аптечке M — хватит на K дней» и дата перерыва цикла — на сервере, с задержкой.
    effect((onCleanup) => {
      const schedule = this.schedule();
      const start = this.startDate();
      const end = this.endDate();
      const unit = this.unit();
      const medicationId = this.writeOff() ? this.medicationId() : null;
      const wanted = medicationId !== null || schedule.mode === DoseScheduleMode.Cycle;
      if (!wanted || !this.canSave()) {
        untracked(() => this.preview.set(null));
        return;
      }
      const timer = setTimeout(() => {
        void this.api.previewCourse({ schedule, startDate: start, endDate: end, unit, medicationId })
          .then((p) => this.preview.set(p))
          .catch(() => this.preview.set(null));
      }, 300);
      onCleanup(() => clearTimeout(timer));
    });

    // Смена «для кого»: назначения и список аптечек зависят от человека.
    effect(() => {
      this.dependentId();
      untracked(() => {
        if (this.source() === 'prescription' && !this.isEdit()) void this.loadPrescriptions();
        const options = this.medChoices();
        if (this.medicationId() && !options.some((m) => m.id === this.medicationId())) this.medicationId.set(null);
      });
    });
  }

  ngOnInit(): void {
    const req = this.request();
    if (req.courseId) {
      void this.loadForEdit(req.courseId);
      return;
    }
    this.dependentId.set(req.dependentId ?? null);
    if (req.recordId) {
      this.source.set('prescription');
      void this.loadPrescriptions(req.recordId, req.prescriptionIndex);
    }
  }

  // ---- Управление формой ----

  protected setSource(source: 'prescription' | 'manual'): void {
    if (this.isEdit()) return;
    this.source.set(source);
    if (source === 'prescription') void this.loadPrescriptions();
  }

  protected setFreq(mode: DoseScheduleMode): void {
    this.freq.set(mode);
  }

  protected addTime(): void {
    const last = this.times().at(-1);
    const next = last ? this.shiftTime(last.at, 4) : '08:00';
    this.times.update((rows) => [...rows, { at: next, units: last?.units ?? 1 }].slice(0, 12));
  }

  protected removeTime(index: number): void {
    this.times.update((rows) => (rows.length > 1 ? rows.filter((_, i) => i !== index) : rows));
  }

  protected setTime(index: number, at: string): void {
    this.times.update((rows) => rows.map((r, i) => (i === index ? { ...r, at } : r)));
  }

  protected setUnits(index: number, units: number): void {
    this.times.update((rows) => rows.map((r, i) => (i === index ? { ...r, units } : r)));
  }

  protected toggleWeekday(day: number): void {
    this.weekdays.update((list) => (list.includes(day) ? list.filter((d) => d !== day) : [...list, day]));
  }

  protected setCyclePreset(on: number, off: number): void {
    this.cycleOn.set(on);
    this.cycleOff.set(off);
  }

  protected setWriteOff(on: boolean): void {
    this.writeOff.set(on);
    if (on && !this.medsLoaded()) void this.loadMedications();
    if (!on) this.medicationId.set(null);
  }

  protected pickPrescription(visit: PrescriptionVisit, item: PrescriptionItem): void {
    this.selectedPrescription.set({ recordId: visit.recordId, index: item.index });
    this.sourceRecordId.set(visit.recordId);
    this.sourceIndex.set(item.index);
    this.prescriptionText.set([item.name, item.dosageInstructions].filter(Boolean).join(' — '));
    this.drugName.set(item.name);
    this.applyDraft(item);
    this.prefilled.set(true);
  }

  protected cancel(): void {
    this.cancelled.emit();
  }

  protected openReminders(): void {
    this.intake.remindersOpen.set(true);
  }

  protected formatVisitDate(date: string): string {
    return formatDayMonth(date);
  }

  protected formatNumber = formatNumber;

  protected async save(): Promise<void> {
    if (!this.canSave() || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    const request = this.buildRequest();
    try {
      const id = this.editingId();
      if (id) await this.api.updateCourse(id, request);
      else await this.api.createCourse(request);
      this.toast.success(id ? 'Курс сохранён' : 'Курс создан — напомним о приёме');
      this.saved.emit();
    } catch (e) {
      this.error.set(e instanceof ApiError && e.message ? e.message : 'Не удалось сохранить курс. Попробуйте ещё раз.');
    } finally {
      this.busy.set(false);
    }
  }

  // ---- Загрузка данных ----

  private async loadPrescriptions(recordId?: string, index?: number): Promise<void> {
    this.prescriptionsLoading.set(true);
    try {
      const list = await this.api.getPrescriptions(this.dependentId() ?? undefined);
      this.prescriptions.set(list);
      if (recordId) {
        const visit = list.find((v) => v.recordId === recordId);
        const item = visit?.items.find((i) => i.index === (index ?? 0)) ?? visit?.items[0];
        if (visit && item) this.pickPrescription(visit, item);
      }
    } catch {
      this.prescriptions.set([]);
    } finally {
      this.prescriptionsLoading.set(false);
    }
  }

  private async loadMedications(): Promise<void> {
    const options: MedOption[] = [];
    try {
      for (const family of this.families.activeFamilies()) {
        const medkits = await this.api.getMedkits(family.id);
        for (const kit of medkits) {
          for (const med of await this.api.getMedications(kit.id)) {
            options.push({ id: med.id, name: med.name, medkitName: kit.name, familyId: family.id, quantity: med.data?.['quantity'] ?? null });
          }
        }
      }
    } catch {
      this.toast.error('Не удалось загрузить аптечку');
    }
    this.medOptions.set(options);
    this.medsLoaded.set(true);
  }

  private async loadForEdit(id: string): Promise<void> {
    this.loading.set(true);
    try {
      this.applyDetail(await this.api.getCourse(id));
      this.editingId.set(id);
      if (this.writeOff()) await this.loadMedications();
    } catch {
      this.error.set('Не удалось загрузить курс.');
    } finally {
      this.loading.set(false);
    }
  }

  // ---- Заполнение ----

  private applyDetail(d: CourseDetail): void {
    const s = d.summary;
    this.dependentId.set(s.subject.kind === 'dependent' ? s.subject.id : null);
    this.drugName.set(s.drugName);
    this.food.set(s.food);
    this.unit.set(s.unit);
    this.startDate.set(s.startDate);
    this.notes.set(d.notes ?? '');
    this.prescriptionText.set(d.prescriptionText);
    this.sourceRecordId.set(d.source?.recordId ?? null);
    this.repeat.set(d.repeatAfterMinutes);
    this.missedAfter.set(d.missedAfterMinutes);
    this.lowStockDays.set(d.lowStockDays);
    this.writeOff.set(s.writeOffEnabled);
    this.medicationId.set(d.medicationId);

    if (s.endDate === null) {
      this.durKind.set('forever');
    } else {
      const days = daysBetween(s.startDate, s.endDate);
      if (days % 30 === 0 && days >= 30) { this.durKind.set('months'); this.durValue.set(days / 30); }
      else if (days % 7 === 0) { this.durKind.set('weeks'); this.durValue.set(days / 7); }
      else { this.durKind.set('days'); this.durValue.set(days); }
    }

    const sch = s.schedule;
    this.freq.set(sch.mode);
    if (sch.times?.length) this.times.set(sch.times.map((t) => ({ at: toTimeInput(t.at), units: t.units })));
    if (sch.mode === DoseScheduleMode.EveryNHours) {
      this.everyHours.set(sch.intervalHours ?? 12);
      this.everyStart.set(toTimeInput(sch.intervalStart) || '08:00');
    }
    if (sch.intervalUnits) this.singleUnits.set(sch.intervalUnits);
    if (sch.weekdays?.length) this.weekdays.set([...sch.weekdays]);
    if (sch.cycleOnDays) this.cycleOn.set(sch.cycleOnDays);
    if (sch.cycleOffDays) this.cycleOff.set(sch.cycleOffDays);
    if (sch.maxPerDay) this.maxPerDay.set(sch.maxPerDay);
  }

  /** Поля из назначения: что распознано — подставляем, остальное пользователь заполнит сам. */
  private applyDraft(item: PrescriptionItem): void {
    const d = item.draft;
    if (d.mode !== null) this.freq.set(d.mode);
    else this.freq.set(DoseScheduleMode.TimesPerDay);
    if (d.unit !== null) this.unit.set(d.unit);
    const units = d.units ?? 1;
    this.singleUnits.set(units);
    if (d.food !== null) this.food.set(d.food);

    if (d.mode === DoseScheduleMode.TimesPerDay || d.mode === null) {
      const times = DEFAULT_TIMES[d.timesPerDay ?? 1] ?? DEFAULT_TIMES[1];
      this.times.set(times.map((at) => ({ at, units })));
    }
    if (d.mode === DoseScheduleMode.EveryNHours && d.intervalHours) this.everyHours.set(d.intervalHours);

    if (d.durationDays) {
      if (d.durationDays % 7 === 0) { this.durKind.set('weeks'); this.durValue.set(d.durationDays / 7); }
      else { this.durKind.set('days'); this.durValue.set(d.durationDays); }
    } else if (d.mode === DoseScheduleMode.AsNeeded) {
      this.durKind.set('forever');
    }
  }

  private buildRequest(): CourseRequest {
    const medicationId = this.writeOff() ? this.medicationId() : null;
    return {
      dependentId: this.dependentId(),
      drugName: this.drugName().trim(),
      schedule: this.schedule(),
      food: this.food(),
      unit: this.unit(),
      startDate: this.startDate(),
      endDate: this.endDate(),
      medicationId,
      writeOff: medicationId !== null,
      repeatAfterMinutes: this.freq() === DoseScheduleMode.AsNeeded ? null : this.repeat(),
      missedAfterMinutes: this.missedAfter(),
      lowStockDays: Math.max(1, Math.floor(Number(this.lowStockDays()) || 5)),
      sourceMedicalRecordId: this.sourceRecordId(),
      sourcePrescriptionIndex: this.sourceRecordId() ? this.sourceIndex() : null,
      prescriptionText: this.prescriptionText(),
      notes: this.notes().trim() || null,
    };
  }

  private shiftTime(at: string, hours: number): string {
    const [h, m] = at.split(':').map(Number);
    const total = (h + hours) % 24;
    return `${String(total).padStart(2, '0')}:${String(m).padStart(2, '0')}`;
  }
}
