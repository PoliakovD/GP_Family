import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, ApiService } from '../../services/api.service';
import { IntakeStateService } from '../../services/intake-state.service';
import { ActionMenuComponent, ActionMenuItem } from '../../shared/action-menu/action-menu.component';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { ToastService } from '../../shared/toast/toast.service';
import { parseLocalBirthDate } from '../../shared/util/birthday-date';
import {
  CourseDetail, CourseHistory, CourseStatus, DoseScheduleMode,
} from '../../models/types';
import {
  WEEKDAY_ORDER, addDays, clock, coversText, daysText, describePattern, foodText, formatDayMonthShort, formatUnits,
  outcomeClass, outcomeLabel, progressFraction, progressText, subjectName, timesText, todayLocal,
  toDateInput, weekdayShort, firstUnits,
} from '../../shared/util/intake-labels';
import { formatDayMonth } from '../../shared/util/date-format';

interface GridRow {
  time: string;
  cells: { date: string; cls: string; label: string; today: boolean }[];
}

interface GridWeek {
  start: string;
  rows: GridRow[];
}

const MISSED_LABELS: Record<number, string> = { 30: '30 минут', 60: 'час', 120: '2 часа', 180: '3 часа', 240: '4 часа' };

/**
 * Карточка курса (макет «Screen - Medication schedule», правая часть «Курсы»): расписание, срок, остаток в
 * аптечке «хватит на N дней», история приёмов сеткой «время × день недели», напоминания. Действия над
 * курсом — изменить, приостановить/возобновить, завершить, удалить.
 */
@Component({
  selector: 'app-intake-course-card',
  imports: [ActionMenuComponent, RouterLink],
  templateUrl: './intake-course-card.component.html',
  styleUrl: './intake-course-card.component.scss',
})
export class IntakeCourseCardComponent {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  protected readonly intake = inject(IntakeStateService);

  readonly courseId = input.required<string>();
  /** Курс удалён — родитель возвращается к списку. */
  readonly removed = output<void>();

  readonly detail = signal<CourseDetail | null>(null);
  readonly history = signal<CourseHistory | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);

  protected readonly Status = CourseStatus;
  protected readonly Mode = DoseScheduleMode;
  protected readonly subjectName = subjectName;
  protected readonly coversText = coversText;

  protected readonly summary = computed(() => this.detail()?.summary ?? null);

  protected readonly scheduleTile = computed(() => {
    const s = this.summary();
    if (!s) return { main: '', sub: '' };
    const times = timesText(s.schedule);
    const food = foodText(s.food);
    if (s.schedule.mode === DoseScheduleMode.AsNeeded) {
      return { main: 'По необходимости', sub: `не чаще ${s.schedule.maxPerDay ?? 1} раз в день` };
    }
    const dose = formatUnits(firstUnits(s.schedule), s.unit);
    return {
      main: s.schedule.mode === DoseScheduleMode.Weekdays || s.schedule.mode === DoseScheduleMode.Cycle
        ? describePattern(s.schedule) : times || describePattern(s.schedule),
      sub: [dose, food].filter(Boolean).join(' · '),
    };
  });

  protected readonly courseTile = computed(() => {
    const d = this.detail();
    const s = this.summary();
    if (!d || !s) return { main: '', sub: '', fraction: 0 };
    const start = formatDayMonthShort(s.startDate);
    const main = s.endDate ? `${short(start)} — ${short(formatDayMonthShort(s.endDate))}` : `с ${short(start)}`;
    const left = s.totalDays === null ? null : Math.max(0, s.totalDays - s.dayNumber);
    return {
      main,
      sub: s.status === CourseStatus.Completed ? 'завершён'
        : left === null ? 'постоянно'
        : left === 0 ? 'последний день' : `осталось ${daysText(left)}`,
      fraction: progressFraction(s.dayNumber, s.totalDays),
    };
  });

  protected readonly stockTile = computed(() => {
    const s = this.summary()?.stock;
    const d = this.detail();
    if (!s || !d) return null;
    const low = s.daysCovered !== null && s.daysCovered <= d.lowStockDays;
    const cover = s.daysCovered === null ? '' : coversText(s.daysCovered);
    const buy = s.shortfall && s.shortfall > 0 ? ` — купите ещё ${Math.ceil(s.shortfall)}` : '';
    return { main: s.quantityText ?? 'количество не указано', sub: `${cover}${buy}`.trim() || (s.medkitName ?? ''), low };
  });

  protected readonly progress = computed(() => {
    const s = this.summary();
    return s ? progressText(s.dayNumber, s.totalDays) : '';
  });

  protected readonly adherenceText = computed(() => {
    const a = this.detail()?.adherence;
    if (!a || a.counted === 0 || a.percent === null) return 'пока нет данных';
    return `${a.onTime} из ${a.counted} вовремя · ${a.percent}%`;
  });

  protected readonly sourceText = computed(() => {
    const src = this.detail()?.source;
    if (!src) return null;
    return `${src.doctor ? `назначил ${src.doctor}, ` : ''}приём ${formatDayMonth(src.recordDate)}`;
  });

  protected readonly reminders = computed(() => {
    const d = this.detail();
    const s = this.summary();
    if (!d || !s) return [];
    const lines: { icon: string; text: string }[] = [];
    if (s.schedule.mode === DoseScheduleMode.AsNeeded) {
      lines.push({ icon: 'ph ph-bell-slash', text: 'Без напоминаний — приём отмечается вручную на странице «Сегодня»' });
    } else {
      lines.push({
        icon: 'ph ph-bell-ringing',
        text: d.repeatAfterMinutes ? `Push в момент приёма, повтор через ${d.repeatAfterMinutes} минут` : 'Push в момент приёма',
      });
      const watchers = d.watchers.filter((w) => w.notifyMissed || w.receiveReminders).map((w) => w.name);
      if (watchers.length > 0) {
        lines.push({
          icon: 'ph ph-users',
          text: s.subject.kind === 'dependent'
            ? `Напоминания получают: ${watchers.join(', ')}`
            : `Сообщить ${watchers.join(', ')}, если не отмечу за ${MISSED_LABELS[d.missedAfterMinutes] ?? d.missedAfterMinutes + ' мин'}`,
        });
      }
    }
    if (s.writeOffEnabled) lines.push({ icon: 'ph ph-package', text: `Предупредить, когда останется на ${daysText(d.lowStockDays)}` });
    return lines;
  });

  protected readonly footnote = computed(() => {
    const s = this.summary();
    const d = this.detail();
    if (!s || !d) return null;
    const stock = s.stock;
    const diary = s.subject.isSelf ? 'записывается в дневник' : null;
    const writeOff = s.writeOffEnabled && stock
      ? `списывает ${formatUnits(firstUnits(s.schedule), s.unit)} из аптечки${stock.medkitName ? ` «${stock.medkitName}»` : ''}` : null;
    const parts = [diary, writeOff].filter((p): p is string => !!p);
    return parts.length > 0 ? `Каждый отмеченный приём ${parts.join(' и ')}` : null;
  });

  protected readonly grid = computed<GridWeek[]>(() => {
    const h = this.history();
    if (!h) return [];
    const today = todayLocal();
    const weeks = new Map<string, GridWeek>();
    const times = new Map<string, Set<string>>();
    for (const c of h.cells) {
      const start = mondayOf(c.date);
      if (!weeks.has(start)) weeks.set(start, { start, rows: [] });
      if (!times.has(start)) times.set(start, new Set());
      times.get(start)!.add(c.time);
    }
    for (const [start, week] of weeks) {
      week.rows = [...times.get(start)!].sort().map((time) => ({
        time,
        cells: Array.from({ length: 7 }, (_, i) => {
          const date = addDays(start, i);
          const cell = h.cells.find((c) => c.date === date && c.time === time);
          return {
            date,
            cls: cell ? outcomeClass(cell.outcome) : 'none',
            label: cell ? `${date} ${time}: ${outcomeLabel(cell.outcome)}` : '',
            today: date === today,
          };
        }),
      }));
    }
    return [...weeks.values()].sort((a, b) => a.start.localeCompare(b.start));
  });

  protected readonly weekdayLabels = WEEKDAY_ORDER.map(weekdayShort);
  protected readonly clock = clock;

  protected readonly menu = computed<ActionMenuItem[]>(() => {
    const s = this.summary();
    const d = this.detail();
    if (!s || !d || !s.canEdit) return [];
    const items: ActionMenuItem[] = [];
    if (s.status === CourseStatus.Active) items.push({ label: 'Приостановить', icon: 'ph ph-pause', handler: () => void this.pause() });
    if (s.status === CourseStatus.Paused) items.push({ label: 'Возобновить', icon: 'ph ph-play', handler: () => void this.resume() });
    if (s.status !== CourseStatus.Completed) items.push({ label: 'Завершить', icon: 'ph ph-flag-checkered', handler: () => void this.complete() });
    if (d.canDelete) items.push({ label: 'Удалить', icon: 'ph ph-trash', danger: true, handler: () => void this.remove() });
    return items;
  });

  constructor() {
    effect(() => {
      const id = this.courseId();
      this.intake.version();
      untracked(() => void this.load(id));
    });
  }

  protected edit(): void {
    this.intake.openForm({ courseId: this.courseId() });
  }

  protected openReminders(): void {
    this.intake.remindersOpen.set(true);
  }

  protected async load(id: string): Promise<void> {
    this.loading.set(this.detail()?.summary.id !== id);
    this.loadError.set(null);
    try {
      const [detail, history] = await Promise.all([this.api.getCourse(id), this.api.getCourseHistory(id, 2)]);
      this.detail.set(detail);
      this.history.set(history);
    } catch (e) {
      this.detail.set(null);
      this.loadError.set(e instanceof ApiError && e.status === 404 ? 'Курс не найден.' : 'Не удалось загрузить курс. Проверьте соединение и попробуйте ещё раз.');
    } finally {
      this.loading.set(false);
    }
  }

  private async pause(): Promise<void> {
    await this.run(() => this.api.pauseCourse(this.courseId()), 'Курс приостановлен');
  }

  private async resume(): Promise<void> {
    await this.run(() => this.api.resumeCourse(this.courseId()), 'Курс возобновлён');
  }

  private async complete(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Завершить курс?',
      message: 'Напоминания прекратятся, курс уйдёт в «Завершённые». История приёмов сохранится.',
      confirmText: 'Завершить',
    });
    if (ok) await this.run(() => this.api.completeCourse(this.courseId()), 'Курс завершён');
  }

  private async remove(): Promise<void> {
    const name = this.summary()?.drugName ?? 'курс';
    const ok = await this.confirm.confirm({
      title: 'Удалить курс?',
      message: `«${name}» и история его приёмов будут удалены. Записи в дневнике останутся.`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;
    try {
      await this.api.deleteCourse(this.courseId());
      this.toast.success('Курс удалён');
      this.intake.changed();
      this.removed.emit();
    } catch (e) {
      this.toast.error(e instanceof ApiError && e.message ? e.message : 'Не удалось удалить курс');
    }
  }

  private async run(action: () => Promise<void>, success: string): Promise<void> {
    try {
      await action();
      this.toast.success(success);
      this.intake.changed();
    } catch (e) {
      this.toast.error(e instanceof ApiError && e.message ? e.message : 'Не удалось выполнить действие');
    }
  }
}

/** Понедельник недели даты «yyyy-MM-dd». */
function mondayOf(dateStr: string): string {
  const d = parseLocalBirthDate(dateStr);
  d.setDate(d.getDate() - ((d.getDay() + 6) % 7));
  return toDateInput(d);
}

/** «12 августа» → «12 авг» для плитки «Курс». */
function short(dayMonth: string): string {
  const [day, month] = dayMonth.split(' ');
  return `${day} ${month.slice(0, 3)}`;
}

