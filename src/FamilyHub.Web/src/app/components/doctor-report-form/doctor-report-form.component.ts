import { Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, ApiService } from '../../services/api.service';
import {
  CreateDoctorReportRequest, DoctorReport, DoctorReportCounts, DoctorReportSubject, DoctorReportSubjectKind,
} from '../../models/types';
import { pluralizeRu } from '../../shared/util/pluralize';
import { DEFAULT_SHARE_DAYS, SHARE_DAY_OPTIONS, expiryPreview } from '../../shared/util/report-labels';

type PeriodPreset = 1 | 3 | 6 | 12 | 'custom';

/** Ключ варианта в выпадающем списке «Чей отчёт». */
const subjectKey = (s: Pick<DoctorReportSubject, 'kind' | 'id'>) => `${s.kind}:${s.id ?? ''}`;
const SELF_KEY = subjectKey({ kind: DoctorReportSubjectKind.Self, id: null });

const pad = (n: number) => String(n).padStart(2, '0');
const toDateInput = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;

/** «Сегодня минус N месяцев» без переполнения дней (31 марта − 1 мес → 28/29 февраля). */
function monthsAgo(months: number, now: Date): Date {
  const d = new Date(now.getFullYear(), now.getMonth() - months, 1);
  const lastDay = new Date(d.getFullYear(), d.getMonth() + 1, 0).getDate();
  return new Date(d.getFullYear(), d.getMonth(), Math.min(now.getDate(), lastDay));
}

/**
 * Форма «Сформировать отчёт для врача» (макет «Screen - Doctor report»): чей отчёт (я, член семьи,
 * подопечный, питомец), период, жалобы, «для кого», срок ссылки и блоки. Разделы, к которым нет доступа
 * (дневник подопечного или чужой без гранта, прививки питомца), выключены и подписаны. Не владеет оверлеем — страница отчётов кладёт её в боковую панель (десктоп) или
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

  /** Кандидаты в пациенты; пока не загружены или есть только «я» — выбора не показываем. */
  readonly subjects = signal<DoctorReportSubject[]>([]);
  readonly subjectKey = signal(SELF_KEY);
  protected readonly subjectKeyOf = subjectKey;

  protected readonly subject = computed(() => this.subjects().find((s) => subjectKey(s) === this.subjectKey()) ?? null);
  protected readonly subjectKind = computed(() => this.subject()?.kind ?? DoctorReportSubjectKind.Self);
  protected readonly isPet = computed(() => this.subject()?.isPet ?? false);
  protected readonly diaryAvailable = computed(() => this.subject()?.diaryAvailable ?? true);
  protected readonly vaccinationsAvailable = computed(() => this.subject()?.vaccinationsAvailable ?? true);

  /** Что попадёт в отчёт о другом человеке и кто об этом узнает. */
  protected readonly subjectHint = computed(() => {
    const s = this.subject();
    if (!s || s.kind === DoctorReportSubjectKind.Self) return '';
    if (s.kind === DoctorReportSubjectKind.User) {
      return `Войдут только записи, которые ${s.name} открыл(а) семье или вы загрузили для этого человека. ` +
        `${s.name} увидит отчёт в своём списке и получит уведомление.`;
    }
    return s.isPet ? 'Отчёт для ветеринара: анализы и приёмы питомца.' : 'Войдут все анализы, приёмы и прививки подопечного.';
  });

  protected readonly diaryHint = computed(() => {
    const s = this.subject();
    if (!s || s.diaryAvailable) return '';
    return s.kind === DoctorReportSubjectKind.User
      ? `Дневник ${s.name} вам не открыт. Открыть его может сам человек: «Настройки → Мои данные → Кто видит моё здоровье».`
      : 'Дневника у подопечных нет — этот раздел не войдёт.';
  });

  protected readonly vaccinationsHint = computed(() => {
    const s = this.subject();
    if (!s || s.vaccinationsAvailable) return '';
    return s.isPet ? 'Прививки питомцев в приложении не ведутся.' : `Прививки ${s.name} вам не открыты.`;
  });

  readonly preset = signal<PeriodPreset>(6);
  readonly from = signal('');
  readonly to = signal('');
  readonly comment = signal('');
  readonly recipient = signal('');
  /** 0 — без ссылки. */
  readonly shareDays = signal<number>(DEFAULT_SHARE_DAYS);

  readonly labs = signal(true);
  // Выключено по умолчанию: врач получает ИИ-трактовку, только если человек сам этого захотел.
  readonly aiSummaries = signal(false);
  readonly medications = signal(true);
  readonly visits = signal(true);
  readonly measurements = signal(true);
  /** Симптомы и заметки длинные — выключены по умолчанию (макет). */
  readonly symptomsNotes = signal(false);
  readonly vaccinations = signal(true);

  readonly counts = signal<DoctorReportCounts | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  private previewTimer: ReturnType<typeof setTimeout> | null = null;
  private previewSeq = 0;

  // Разделы, к которым нет доступа, не уходят в запрос, даже если переключатель остался включённым.
  protected readonly measurementsOn = computed(() => this.measurements() && this.diaryAvailable());
  protected readonly symptomsNotesOn = computed(() => this.symptomsNotes() && this.diaryAvailable());
  protected readonly vaccinationsOn = computed(() => this.vaccinations() && this.vaccinationsAvailable());

  protected readonly anyBlock = computed(() =>
    this.labs() || this.aiSummaries() || this.medications() || this.visits() || this.measurementsOn()
    || this.symptomsNotesOn() || this.vaccinationsOn());

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
    void this.loadSubjects();
  }

  private async loadSubjects(): Promise<void> {
    try {
      this.subjects.set(await this.api.getDoctorReportSubjects());
    } catch {
      this.subjects.set([]); // без списка форма работает как раньше — отчёт о себе
    }
  }

  protected subjectLabel(s: DoctorReportSubject): string {
    if (s.kind === DoctorReportSubjectKind.Self) return `Я — ${s.name}`;
    return s.isPet ? `${s.name} — питомец` : s.name;
  }

  protected selectSubject(key: string): void {
    this.subjectKey.set(key);
    this.schedulePreview();
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
      const counts = await this.api.previewDoctorReport(this.from(), this.to(), this.subjectKind(), this.subject()?.id ?? null);
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
      includeMeasurements: this.measurementsOn(),
      includeSymptomsNotes: this.symptomsNotesOn(),
      includeVaccinations: this.vaccinationsOn(),
      recipient: this.recipient().trim() || null,
      patientComment: this.comment().trim() || null,
      shareDays: this.shareDays() || null,
      subjectKind: this.subjectKind(),
      subjectId: this.subject()?.id ?? null,
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
