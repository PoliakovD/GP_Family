import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, ApiService } from '../../services/api.service';
import { VaccinationStateService, type VaccinationAddRequest } from '../../services/vaccination-state.service';
import { ToastService } from '../../shared/toast/toast.service';
import { AvatarComponent } from '../../shared/avatar/avatar.component';
import {
  BulkMarkVaccinationItem, VaccinationDatePrecision, VaccinationKind, VaccinationPersonSchedule,
  VaccinationRecognizedItem, VaccinationScheduleItem, VaccinationStatus, VaccinationSubject, VaccineCatalogSeries,
  VaccineGroup,
} from '../../models/types';
import { todayLocal } from '../../shared/util/intake-labels';
import type { VaccinationSavedInfo } from '../../services/vaccination-state.service';

export interface VaccinationSavedEvent {
  /** Заполнено только для «одной прививки»/быстрой отметки — показывает шторку «Сохранено».
   * Массовая отметка (по календарю/сертификату) сама сообщает итог тостом, шторку не открывает. */
  info?: VaccinationSavedInfo;
}

type Mode = 'one' | 'bulk' | 'certificate';
type Choice = 'done' | 'unknown' | 'had';

interface CalendarRow {
  seriesCode: string;
  seriesName: string;
  doseIndex: number;
  label: string;
  group: VaccineGroup;
  closedByDisease: boolean;
  alreadyDone: boolean;
  doneDate: string | null;
}

interface CertRow extends VaccinationRecognizedItem {
  checked: boolean;
  seriesCodeEdit: string | null;
  dateEdit: string | null;
}

/**
 * «Добавить прививки» (макет «Screen - Vaccination») — три способа: одна прививка (автокомплит по
 * каталогу + дата + файл), отметить по календарю (чек-лист прошлого без обязательных дат) и фото
 * сертификата (распознавание, подтверждение сохраняет фото). Сама модалка не владеет оверлеем —
 * страница кладёт её в side-panel/bottom-sheet (см. VaccinationsPageComponent).
 */
@Component({
  selector: 'app-vaccination-add-modal',
  imports: [AvatarComponent, FormsModule],
  templateUrl: './vaccination-add-modal.component.html',
  styleUrl: './vaccination-add-modal.component.scss',
})
export class VaccinationAddModalComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  protected readonly vaccinations = inject(VaccinationStateService);

  readonly request = input.required<VaccinationAddRequest>();
  readonly saved = output<VaccinationSavedEvent>();
  readonly cancelled = output<void>();

  protected readonly Status = VaccinationStatus;
  protected readonly Group = VaccineGroup;

  protected readonly mode = signal<Mode>('one');
  protected readonly subjects = signal<VaccinationSubject[]>([]);
  protected readonly subjectKind = signal<'user' | 'dependent'>('user');
  protected readonly subjectId = signal<string>('');
  protected readonly personSchedule = signal<VaccinationPersonSchedule | null>(null);

  protected readonly currentSubject = computed(() =>
    this.subjects().find((s) => s.kind === this.subjectKind() && s.id === this.subjectId()) ?? null);

  // ---- Одна прививка ----
  protected readonly query = signal('');
  protected readonly selectedSeries = signal<VaccineCatalogSeries | null>(null);
  protected readonly vaccineName = signal('');
  protected readonly date = signal(todayLocal());
  protected readonly file = signal<File | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly matches = computed<VaccineCatalogSeries[]>(() => {
    const q = this.query().trim().toLowerCase();
    if (!q) return [];
    const norm = (s: string) => s.toLowerCase();
    return (this.vaccinations.catalog() ?? [])
      .filter((s) => norm(s.name).includes(q) || norm(s.shortName).includes(q)
        || s.diseases.some((d) => norm(d).includes(q)) || s.tradeNames.some((t) => norm(t).includes(q)))
      .slice(0, 8);
  });

  protected readonly hasMoreDoses = computed(() => {
    const series = this.selectedSeries();
    if (!series) return false;
    const idx = this.suggestedDoseIndex(series);
    return idx < series.doses.length - 1 || series.repeatEveryYears !== null;
  });

  // ---- По календарю ----
  protected readonly rows = signal<CalendarRow[]>([]);
  protected readonly choices = signal<Record<string, Choice | undefined>>({});
  protected readonly years = signal<Record<string, string>>({});

  protected readonly nationalRows = computed(() => this.rows().filter((r) => r.group === VaccineGroup.National));
  protected readonly adultRows = computed(() => this.rows().filter((r) => r.group === VaccineGroup.Adult));
  protected readonly bulkCount = computed(() => Object.values(this.choices()).filter((c) => c !== undefined).length);

  // ---- Фото сертификата ----
  protected readonly certFiles = signal<File[]>([]);
  protected readonly certStage = signal<'pick' | 'recognizing' | 'review' | 'saving'>('pick');
  protected readonly certProgress = signal(0);
  protected readonly certItems = signal<CertRow[]>([]);
  protected readonly certError = signal<string | null>(null);
  protected readonly certChecked = computed(() => this.certItems().filter((i) => i.checked).length);

  async ngOnInit(): Promise<void> {
    // Каталог нужен сразу для чек-листа «по календарю» (closedByDisease) — ждём его перед
    // загрузкой графика человека, а не параллельно (иначе строки не узнают о болезнях, пока
    // каталог ещё не пришёл, и чипы «болела» просто не появятся).
    await this.vaccinations.ensureCatalog();
    void this.loadSubjects();
  }

  protected setMode(mode: Mode): void {
    this.mode.set(mode);
    this.error.set(null);
  }

  protected selectSubject(s: VaccinationSubject): void {
    this.subjectKind.set(s.kind);
    this.subjectId.set(s.id);
    void this.loadPersonSchedule();
  }

  protected rowKey(row: CalendarRow): string {
    return `${row.seriesCode}:${row.doseIndex}`;
  }

  protected choiceOf(row: CalendarRow): Choice | undefined {
    return this.choices()[this.rowKey(row)];
  }

  protected setChoice(row: CalendarRow, choice: Choice): void {
    this.choices.update((m) => {
      const key = this.rowKey(row);
      return { ...m, [key]: m[key] === choice ? undefined : choice };
    });
  }

  protected yearOf(row: CalendarRow): string {
    return this.years()[this.rowKey(row)] ?? '';
  }

  protected setYear(row: CalendarRow, value: string): void {
    this.years.update((m) => ({ ...m, [this.rowKey(row)]: value }));
  }

  // ---- Одна прививка ----

  protected pickSeries(series: VaccineCatalogSeries): void {
    this.selectedSeries.set(series);
    this.query.set(series.name);
  }

  protected clearSeries(): void {
    this.selectedSeries.set(null);
  }

  protected onFileSelected(files: FileList | null): void {
    this.file.set(files && files.length > 0 ? files[0] : null);
  }

  protected async submitOne(): Promise<void> {
    if (this.busy()) return;
    if (!this.subjectId()) {
      // Раньше «Сохранить» при незагруженном списке людей молча ничего не делал.
      this.error.set('Подождите, загружаем список людей…');
      return;
    }
    const series = this.selectedSeries();
    const customName = this.query().trim();
    if (!series && !customName) {
      this.error.set('Укажите, от чего прививка.');
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      const item = await this.api.createVaccination({
        subjectKind: this.subjectKind(),
        subjectId: this.subjectId(),
        seriesCode: series?.code ?? null,
        doseIndex: series ? this.suggestedDoseIndex(series) : null,
        customName: series ? null : customName,
        vaccineName: this.vaccineName().trim() || null,
        kind: VaccinationKind.Done,
        date: this.date() || null,
        datePrecision: this.date() ? VaccinationDatePrecision.Day : null,
        certificateId: null,
        requestWellbeingCheck: false,
      });
      if (this.file() && item.recordId) {
        try {
          await this.api.uploadVaccinationAttachment(item.recordId, this.file()!);
        } catch {
          this.toast.error('Прививка сохранена, но файл не удалось прикрепить.');
        }
      }
      const subject = this.currentSubject();
      this.saved.emit({ info: subject ? { subject, item, reactionHint: series?.reactionHint ?? null } : undefined });
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Не удалось сохранить прививку.');
    } finally {
      this.busy.set(false);
    }
  }

  // ---- По календарю ----

  protected async submitBulk(): Promise<void> {
    if (this.busy()) return;
    if (!this.subjectId()) {
      this.error.set('Подождите, загружаем список людей…');
      return;
    }
    // Год вида «98» или «20» раньше молча отбрасывался — прививка сохранялась без даты.
    const badYear = this.rows().find((row) => this.choiceOf(row) === 'done' && this.yearOf(row).trim() !== '' && !/^\d{4}$/.test(this.yearOf(row).trim()));
    if (badYear) {
      this.error.set(`Год для «${badYear.seriesName ?? 'прививки'}» — четыре цифры, например 1998.`);
      return;
    }
    const items: BulkMarkVaccinationItem[] = [];
    for (const row of this.rows()) {
      const choice = this.choiceOf(row);
      if (!choice) continue;
      if (choice === 'done') {
        const year = this.yearOf(row).trim();
        const date = /^\d{4}$/.test(year) ? `${year}-01-01` : null;
        items.push({
          seriesCode: row.seriesCode, doseIndex: row.doseIndex, kind: VaccinationKind.Done,
          date, datePrecision: date ? VaccinationDatePrecision.Year : null,
        });
      } else if (choice === 'unknown') {
        items.push({ seriesCode: row.seriesCode, doseIndex: row.doseIndex, kind: VaccinationKind.Unknown, date: null, datePrecision: null });
      } else {
        items.push({ seriesCode: row.seriesCode, doseIndex: row.doseIndex, kind: VaccinationKind.HadDisease, date: null, datePrecision: null });
      }
    }
    if (items.length === 0) {
      this.error.set('Отметьте хотя бы одну прививку.');
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      const result = await this.api.bulkMarkVaccinations({ subjectKind: this.subjectKind(), subjectId: this.subjectId(), items, certificateId: null });
      this.toast.success(`Отмечено ${result.saved}.`);
      this.saved.emit({});
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Не удалось сохранить.');
    } finally {
      this.busy.set(false);
    }
  }

  // ---- Фото сертификата ----

  protected onCertFilesSelected(files: FileList | null): void {
    if (!files) return;
    this.certFiles.update((list) => [...list, ...Array.from(files)]);
  }

  protected removeCertFile(index: number): void {
    this.certFiles.update((list) => list.filter((_, i) => i !== index));
  }

  protected async recognizeCertificate(): Promise<void> {
    if (this.certFiles().length === 0) return;
    this.certStage.set('recognizing');
    this.certError.set(null);
    this.certProgress.set(0);
    try {
      const response = await this.api.recognizeVaccinationCertificate(this.certFiles(), (p) => this.certProgress.set(p));
      if (!response.success) {
        this.certError.set(response.error ?? 'Не удалось распознать сертификат.');
        this.certStage.set('pick');
        return;
      }
      if (response.items.length === 0) {
        this.certError.set('На фото не нашлось ни одной записи о прививке.');
        this.certStage.set('pick');
        return;
      }
      this.certItems.set(response.items.map((i) => ({ ...i, checked: !i.needsReview, seriesCodeEdit: i.seriesCode, dateEdit: i.date })));
      this.certStage.set('review');
    } catch (e) {
      this.certError.set(e instanceof ApiError ? e.message : 'Распознавание недоступно — попробуйте отметить по календарю.');
      this.certStage.set('pick');
    }
  }

  protected toggleCertItem(index: number): void {
    this.certItems.update((items) => items.map((it, i) => (i === index ? { ...it, checked: !it.checked } : it)));
  }

  protected setCertSeries(index: number, code: string): void {
    this.certItems.update((items) => items.map((it, i) => (i === index ? { ...it, seriesCodeEdit: code || null } : it)));
  }

  protected setCertDate(index: number, date: string): void {
    this.certItems.update((items) => items.map((it, i) => (i === index ? { ...it, dateEdit: date || null } : it)));
  }

  protected async confirmCertificate(): Promise<void> {
    const items = this.certItems()
      .filter((i) => i.checked && i.seriesCodeEdit)
      .map((i) => ({
        seriesCode: i.seriesCodeEdit!,
        doseIndex: this.suggestedDoseIndexForCode(i.seriesCodeEdit!),
        kind: VaccinationKind.Done,
        date: i.dateEdit,
        datePrecision: i.dateEdit ? VaccinationDatePrecision.Day : null,
      }));
    if (items.length === 0) {
      this.certError.set('Отметьте хотя бы одну находку с определённой прививкой из календаря.');
      return;
    }
    this.certStage.set('saving');
    this.certError.set(null);
    try {
      const result = await this.api.confirmVaccinationCertificate(this.subjectKind(), this.subjectId(), this.certFiles(), items);
      this.toast.success(`Сохранено ${result.saved} прививок.`);
      this.saved.emit({});
    } catch (e) {
      this.certError.set(e instanceof ApiError ? e.message : 'Не удалось сохранить.');
      this.certStage.set('review');
    }
  }

  // ---- Общее ----

  private suggestedDoseIndex(series: VaccineCatalogSeries): number {
    return this.suggestedDoseIndexForCode(series.code);
  }

  private suggestedDoseIndexForCode(code: string): number {
    const open = (this.personSchedule()?.byAge ?? [])
      .filter((i: VaccinationScheduleItem) => i.seriesCode === code && i.status !== VaccinationStatus.Done && i.status !== VaccinationStatus.HadDisease)
      .map((i) => i.doseIndex);
    return open.length > 0 ? Math.min(...open) : 0;
  }

  private async loadSubjects(): Promise<void> {
    try {
      const overview = await this.api.getVaccinationsOverview();
      const list = overview.people.map((p) => p.subject).filter((s) => s.canEdit);
      this.subjects.set(list);
      const req = this.request();
      const chosen = (req.subjectKind && req.subjectId
        ? list.find((s) => s.kind === req.subjectKind && s.id === req.subjectId)
        : null) ?? list.find((s) => s.isSelf) ?? list[0];
      if (chosen) this.selectSubject(chosen);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Не удалось загрузить список людей.');
    }
  }

  private async loadPersonSchedule(): Promise<void> {
    if (!this.subjectId()) return;
    try {
      const schedule = await this.api.getVaccinationPersonSchedule(this.subjectKind(), this.subjectId());
      this.personSchedule.set(schedule);
      this.rebuildRows(schedule);
    } catch {
      this.personSchedule.set(null);
      this.rows.set([]);
    }
  }

  private rebuildRows(schedule: VaccinationPersonSchedule): void {
    const catalog = this.vaccinations.catalog() ?? [];
    const byCode = new Map(catalog.map((c) => [c.code, c]));
    const rows: CalendarRow[] = [];
    for (const item of schedule.byAge) {
      if (item.group === VaccineGroup.Epidemic) continue; // добровольные — не в этом чек-листе
      const series = byCode.get(item.seriesCode);
      rows.push({
        seriesCode: item.seriesCode, seriesName: item.seriesName, doseIndex: item.doseIndex, label: item.label,
        group: item.group, closedByDisease: series?.closedByDisease ?? false,
        alreadyDone: item.status === VaccinationStatus.Done || item.status === VaccinationStatus.HadDisease,
        doneDate: item.date,
      });
    }
    this.rows.set(rows);
    this.choices.set({});
    this.years.set({});
  }
}
