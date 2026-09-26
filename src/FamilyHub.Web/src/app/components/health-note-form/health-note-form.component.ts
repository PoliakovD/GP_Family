import { Component, OnInit, WritableSignal, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, ApiService } from '../../services/api.service';
import { ToastService } from '../../shared/toast/toast.service';
import {
  HealthNote, HealthNoteCatalog, HealthNoteInput, HealthNoteKind,
} from '../../models/types';
import {
  BODY_AREA_LABELS, HEALTH_KINDS, SLEEP_QUALITY_LABELS, WELLBEING_FACTOR_LABELS, WELLBEING_LEVELS,
  formatDuration, fromLocalInput, kindMeta, severityWord, toLocalInput,
} from '../../shared/util/health-note-labels';

/**
 * Форма записи дневника (шесть типов). Не владеет оверлеем: страница дневника кладёт её в
 * `app-side-panel` (десктоп) или `app-bottom-sheet` (мобайл) — см. HealthNotesTabComponent.
 * Меняется только середина по типу; «Когда», заметка и кнопки остаются на месте (макет
 * «Screen - Diary»). В режиме правки тип менять нельзя.
 */
@Component({
  selector: 'app-health-note-form',
  imports: [FormsModule],
  templateUrl: './health-note-form.component.html',
  styleUrl: './health-note-form.component.scss',
})
export class HealthNoteFormComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

  /** Каталог замеров/ключей; null, пока страница его ещё грузит. */
  readonly catalog = input<HealthNoteCatalog | null>(null);
  /** Правка существующей записи; null — создание. */
  readonly note = input<HealthNote | null>(null);
  readonly saved = output<void>();
  readonly cancelled = output<void>();

  protected readonly kinds = HEALTH_KINDS;
  protected readonly Kind = HealthNoteKind;
  protected readonly severities = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
  protected readonly wellbeingLevels = WELLBEING_LEVELS;
  protected readonly bodyAreaLabels = BODY_AREA_LABELS;
  protected readonly factorLabels = WELLBEING_FACTOR_LABELS;
  protected readonly sleepQualities = [1, 2, 3].map((q) => ({ value: q, label: SLEEP_QUALITY_LABELS[q] }));
  protected readonly severityWord = severityWord;
  protected readonly kindMeta = kindMeta;

  readonly kind = signal<HealthNoteKind>(HealthNoteKind.Symptom);
  readonly when = signal(toLocalInput(new Date()));
  readonly text = signal('');
  readonly includeInDoctorQuestions = signal(false);

  // Симптом / лекарство
  readonly title = signal('');
  readonly severity = signal<number | null>(null);
  readonly areas = signal<string[]>([]);
  readonly detail = signal('');
  readonly recents = signal<string[]>([]);
  readonly dose = signal('');

  // Замер
  readonly metricCode = signal('blood_pressure');
  readonly value = signal<number | null>(null);
  readonly value2 = signal<number | null>(null);
  readonly addPulse = signal(false);
  readonly pulse = signal<number | null>(null);

  // Самочувствие
  readonly score = signal<number | null>(null);
  readonly factors = signal<string[]>([]);

  // Сон
  readonly bedTime = signal('23:30');
  readonly wakeTime = signal('07:00');
  readonly quality = signal(3);

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  protected readonly isEdit = computed(() => this.note() !== null);

  protected readonly metric = computed(() =>
    this.catalog()?.metrics.find((m) => m.code === this.metricCode()) ?? null);

  /** NaN, пока поля времени пусты/некорректны — `canSave` тогда false, а ISO не строится. */
  protected readonly sleepMinutes = computed(() => {
    const { bed, wake } = this.sleepRange();
    return Math.round((wake.getTime() - bed.getTime()) / 60000);
  });

  protected readonly sleepPreview = computed(() => formatDuration(this.sleepMinutes()));

  protected readonly canSave = computed(() => {
    switch (this.kind()) {
      case HealthNoteKind.Symptom:
        return this.title().trim() !== '' && this.severity() !== null;
      case HealthNoteKind.Metric: {
        const m = this.metric();
        if (!m || this.value() === null) return false;
        if (m.hasSecondValue && this.value2() === null) return false;
        return !(this.addPulse() && this.pulse() === null);
      }
      case HealthNoteKind.Wellbeing:
        return this.score() !== null;
      case HealthNoteKind.MedicationIntake:
        return this.title().trim() !== '';
      case HealthNoteKind.Sleep:
        return this.sleepMinutes() > 0;
      default:
        return this.text().trim() !== '';
    }
  });

  ngOnInit(): void {
    const n = this.note();
    if (n) this.prefill(n);
    void this.loadRecents();
  }

  protected selectKind(kind: HealthNoteKind): void {
    if (this.isEdit()) return;
    this.kind.set(kind);
    this.error.set(null);
    void this.loadRecents();
  }

  protected toggleIn(list: WritableSignal<string[]>, key: string): void {
    list.update((l) => (l.includes(key) ? l.filter((k) => k !== key) : [...l, key]));
  }

  protected setTitle(t: string): void {
    this.title.set(t);
  }

  /** Метрика меняется — второе значение/пульс относятся к предыдущей, сбрасываем. */
  protected selectMetric(code: string): void {
    this.metricCode.set(code);
    this.value.set(null);
    this.value2.set(null);
    this.addPulse.set(false);
    this.pulse.set(null);
  }

  /** Для сна «Когда» — дата пробуждения; время суток берётся из полей «Лёг»/«Встал». */
  protected onWhenDate(date: string): void {
    if (date) this.when.set(`${date}T${this.when().slice(11, 16) || '00:00'}`);
  }

  private async loadRecents(): Promise<void> {
    const k = this.kind();
    if (k !== HealthNoteKind.Symptom && k !== HealthNoteKind.MedicationIntake) {
      this.recents.set([]);
      return;
    }
    try {
      this.recents.set(await this.api.getRecentHealthNoteTitles(k));
    } catch {
      this.recents.set([]); // чипы «Недавние» — удобство, а не необходимость
    }
  }

  private prefill(n: HealthNote): void {
    this.kind.set(n.kind);
    this.when.set(toLocalInput(new Date(n.occurredAt)));
    this.text.set(n.text ?? '');
    this.includeInDoctorQuestions.set(n.includeInDoctorQuestions);
    this.title.set(n.title ?? '');
    if (n.symptom) {
      this.severity.set(n.symptom.severity);
      this.areas.set([...(n.symptom.areas ?? [])]);
      this.detail.set(n.symptom.detail ?? '');
    }
    if (n.metric) {
      this.metricCode.set(n.metric.code);
      this.value.set(n.metric.value);
      this.value2.set(n.metric.value2 ?? null);
    }
    if (n.wellbeing) {
      this.score.set(n.wellbeing.score);
      this.factors.set([...(n.wellbeing.factors ?? [])]);
    }
    if (n.intake) this.dose.set(n.intake.dose ?? '');
    if (n.sleep) {
      const clock = (iso: string) => toLocalInput(new Date(iso)).slice(11, 16);
      this.bedTime.set(clock(n.sleep.bedTime));
      this.wakeTime.set(clock(n.sleep.wakeTime));
      this.quality.set(n.sleep.quality);
    }
  }

  /** Сон: дата — «Когда» (день пробуждения); если «Лёг» позже «Встал» по часам — легли накануне. */
  private sleepRange(): { bed: Date; wake: Date } {
    const day = this.when().slice(0, 10);
    const wake = new Date(`${day}T${this.wakeTime()}`);
    let bed = new Date(`${day}T${this.bedTime()}`);
    if (bed.getTime() >= wake.getTime()) bed = new Date(bed.getTime() - 24 * 3600 * 1000);
    return { bed, wake };
  }

  private buildSleep() {
    const { bed, wake } = this.sleepRange();
    return { bedTime: bed.toISOString(), wakeTime: wake.toISOString(), quality: this.quality() };
  }

  private buildInput(): HealthNoteInput {
    const kind = this.kind();
    const base: HealthNoteInput = {
      kind,
      occurredAt: fromLocalInput(this.when()),
      text: this.text().trim() || null,
    };
    switch (kind) {
      case HealthNoteKind.Symptom:
        return {
          ...base,
          title: this.title().trim(),
          symptom: { severity: this.severity()!, areas: this.areas(), detail: this.detail().trim() || null },
        };
      case HealthNoteKind.Metric:
        return { ...base, metric: { code: this.metricCode(), value: this.value()!, value2: this.metric()?.hasSecondValue ? this.value2() : null } };
      case HealthNoteKind.Wellbeing:
        return { ...base, wellbeing: { score: this.score()!, factors: this.factors() } };
      case HealthNoteKind.MedicationIntake:
        return { ...base, title: this.title().trim(), intake: { dose: this.dose().trim() || null } };
      case HealthNoteKind.Sleep: {
        const sleep = this.buildSleep();
        return { ...base, occurredAt: sleep.wakeTime, sleep };
      }
      default:
        return { ...base, includeInDoctorQuestions: this.includeInDoctorQuestions() };
    }
  }

  protected async save(): Promise<void> {
    if (!this.canSave() || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const input = this.buildInput();
      const existing = this.note();
      if (existing) {
        await this.api.updateHealthNote(existing.id, input);
      } else {
        await this.api.createHealthNote(input);
        // «Пульс добавляется к давлению одной строкой» — отдельной записью в то же время.
        if (this.kind() === HealthNoteKind.Metric && this.addPulse() && this.pulse() !== null) {
          await this.api.createHealthNote({
            kind: HealthNoteKind.Metric,
            occurredAt: input.occurredAt,
            metric: { code: 'pulse', value: this.pulse()!, value2: null },
          });
        }
      }
      this.toast.success(existing ? 'Запись обновлена' : 'Запись сохранена');
      this.saved.emit();
    } catch (e) {
      // 400 несёт человекочитаемую причину валидации; остальное — общий текст.
      this.error.set(e instanceof ApiError && e.status === 400 && e.message
        ? e.message
        : 'Не удалось сохранить запись. Попробуйте ещё раз.');
    } finally {
      this.busy.set(false);
    }
  }
}
