import { Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, ApiService } from '../../services/api.service';
import { CreateDoctorReportRequest, DoctorReport, DoctorReportCounts } from '../../models/types';
import { pluralizeRu } from '../../shared/util/pluralize';
import { DEFAULT_SHARE_DAYS, SHARE_DAY_OPTIONS, expiryPreview } from '../../shared/util/report-labels';

type PeriodPreset = 1 | 3 | 6 | 12 | 'custom';

const pad = (n: number) => String(n).padStart(2, '0');
const toDateInput = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;

/** «Сегодня минус N месяцев» без переполнения дней (31 марта − 1 мес → 28/29 февраля). */
function monthsAgo(months: number, now: Date): Date {
  const d = new Date(now.getFullYear(), now.getMonth() - months, 1);
  const lastDay = new Date(d.getFullYear(), d.getMonth() + 1, 0).getDate();
  return new Date(d.getFullYear(), d.getMonth(), Math.min(now.getDate(), lastDay));
}

/**
 * Форма «Сформировать отчёт для врача» (макет «Screen - Doctor report»): период, жалобы, «для кого»,
 * срок ссылки и блоки. Не владеет оверлеем — страница отчётов кладёт её в боковую панель (десктоп) или
 * нижний лист (мобайл). Генерация синхронная (PDF в Gotenberg), поэтому кнопка блокируется и
 * показывает «Формируем…».
 */
@Component({
  selector: 'app-doctor-report-form',
  imports: [FormsModule],
  templateUrl: './doctor-report-form.component.html',
  styleUrl: './doctor-report-form.component.scss',
})
export class DoctorReportFormComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);

  /** Открыто из дневника («В отчёт для врача»): блоки дневника включены сразу. */
  readonly fromDiary = input(false);
  readonly created = output<DoctorReport>();
  readonly cancelled = output<void>();

  protected readonly presets: { value: PeriodPreset; label: string }[] = [
    { value: 1, label: '1 мес' }, { value: 3, label: '3 мес' }, { value: 6, label: '6 мес' }, { value: 12, label: '1 год' },
  ];
  protected readonly shareOptions = SHARE_DAY_OPTIONS;

  readonly preset = signal<PeriodPreset>(6);
  readonly from = signal('');
  readonly to = signal('');
  readonly comment = signal('');
  readonly recipient = signal('');
  /** 0 — без ссылки. */
  readonly shareDays = signal<number>(DEFAULT_SHARE_DAYS);

  readonly labs = signal(true);
  readonly aiSummaries = signal(true);
  readonly medications = signal(true);
  readonly visits = signal(true);
  readonly measurements = signal(true);
  /** Симптомы и заметки длинные — выключены по умолчанию (макет). */
  readonly symptomsNotes = signal(false);

  readonly counts = signal<DoctorReportCounts | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  private previewTimer: ReturnType<typeof setTimeout> | null = null;
  private previewSeq = 0;

  protected readonly anyBlock = computed(() =>
    this.labs() || this.aiSummaries() || this.medications() || this.visits() || this.measurements() || this.symptomsNotes());

  protected readonly periodValid = computed(() => !!this.from() && !!this.to() && this.from() <= this.to());

  protected readonly canSubmit = computed(() => this.periodValid() && this.anyBlock() && !this.busy());

  protected readonly countsText = computed(() => {
    const c = this.counts();
    if (!c) return '';
    return `За период: ${c.analyses} ${pluralizeRu(c.analyses, 'анализ', 'анализа', 'анализов')}, ` +
      `${c.visits} ${pluralizeRu(c.visits, 'приём', 'приёма', 'приёмов')}, ` +
      `${c.diaryEntries} ${pluralizeRu(c.diaryEntries, 'запись', 'записи', 'записей')} дневника`;
  });

  protected readonly linkHint = computed(() => this.shareDays() === 0
    ? 'Ссылки не будет: PDF можно скачать и распечатать или создать ссылку позже.'
    : `Ссылка будет работать до ${expiryPreview(this.shareDays())}. Отозвать можно в любой момент.`);

  ngOnInit(): void {
    if (this.fromDiary()) this.symptomsNotes.set(true);
    this.applyPreset(6);
  }

  ngOnDestroy(): void {
    if (this.previewTimer) clearTimeout(this.previewTimer);
  }

  protected applyPreset(preset: PeriodPreset): void {
    this.preset.set(preset);
    if (preset !== 'custom') {
      const now = new Date();
      this.from.set(toDateInput(monthsAgo(preset, now)));
      this.to.set(toDateInput(now));
    }
    this.schedulePreview();
  }

  protected setDate(which: 'from' | 'to', value: string): void {
    (which === 'from' ? this.from : this.to).set(value);
    this.preset.set('custom');
    this.schedulePreview();
  }

  /** Счётчик пересчитывается с задержкой: при вводе даты с клавиатуры не шлём запрос на каждую цифру. */
  private schedulePreview(): void {
    if (this.previewTimer) clearTimeout(this.previewTimer);
    this.previewTimer = setTimeout(() => void this.loadPreview(), 300);
  }

  private async loadPreview(): Promise<void> {
    if (!this.periodValid()) {
      this.counts.set(null);
      return;
    }
    const seq = ++this.previewSeq;
    try {
      const counts = await this.api.previewDoctorReport(this.from(), this.to());
      if (seq === this.previewSeq) this.counts.set(counts);
    } catch {
      if (seq === this.previewSeq) this.counts.set(null); // счётчик — подсказка, не блокирует создание
    }
  }

  protected async submit(): Promise<void> {
    if (!this.canSubmit()) return;
    this.busy.set(true);
    this.error.set(null);
    const request: CreateDoctorReportRequest = {
      periodFrom: this.from(),
      periodTo: this.to(),
      includeLabs: this.labs(),
      includeAiSummaries: this.aiSummaries(),
      includeMedications: this.medications(),
      includeVisits: this.visits(),
      includeMeasurements: this.measurements(),
      includeSymptomsNotes: this.symptomsNotes(),
      recipient: this.recipient().trim() || null,
      patientComment: this.comment().trim() || null,
      shareDays: this.shareDays() || null,
    };
    try {
      this.created.emit(await this.api.createDoctorReport(request));
    } catch (e) {
      // 400/409/422/503 несут человекочитаемую причину в message (см. DoctorReportEndpoints).
      this.error.set(e instanceof ApiError && e.message
        ? e.message
        : 'Не удалось сформировать отчёт. Попробуйте ещё раз.');
    } finally {
      this.busy.set(false);
    }
  }
}
