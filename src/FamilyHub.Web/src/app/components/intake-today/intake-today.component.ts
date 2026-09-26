import { Component, OnDestroy, OnInit, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, ApiService } from '../../services/api.service';
import { IntakeStateService } from '../../services/intake-state.service';
import { ActionMenuComponent, ActionMenuItem } from '../../shared/action-menu/action-menu.component';
import { AvatarComponent } from '../../shared/avatar/avatar.component';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { ToastService } from '../../shared/toast/toast.service';
import {
  DayPeriod, DoseAction, DoseOutcome, IntakeSubject, IntakeToday, LowStock, TodayAsNeeded, TodayDose, WeekDay,
} from '../../models/types';
import {
  agoText, clock, clockOfIso, coversText, foodText, formatUnits, headingDate, progressText, shortDate,
  subjectKey, subjectName,
} from '../../shared/util/intake-labels';

interface Row {
  item: TodayDose;
  sub: string;
  time: string;
  actions: ActionMenuItem[];
  highlighted: boolean;
}

interface Group {
  period: DayPeriod;
  title: string;
  icon: string;
  isNow: boolean;
  rows: Row[];
}

const PERIODS: { period: DayPeriod; title: string; icon: string }[] = [
  { period: DayPeriod.Morning, title: 'Утро', icon: 'ph-duotone ph-sun-horizon' },
  { period: DayPeriod.Day, title: 'День', icon: 'ph-duotone ph-sun' },
  { period: DayPeriod.Evening, title: 'Вечер', icon: 'ph-duotone ph-moon' },
];

const REFRESH_MS = 60_000;

/**
 * Экран «Сегодня» (макет «Screen - Medication schedule»): чек-лист приёмов по времени дня для всей семьи или
 * одного человека. Текущий приём — одна крупная карточка с кнопкой «Принял»; принятое свёрнуто серым, будущее —
 * пустым квадратом. Справа — пропуски родственников, заканчивающийся запас и неделя. Любое действие — через
 * API (списание из аптечки и запись в дневник делает сервер), после чего все экраны перечитывают данные.
 */
@Component({
  selector: 'app-intake-today',
  imports: [ActionMenuComponent, AvatarComponent, RouterLink],
  templateUrl: './intake-today.component.html',
  styleUrl: './intake-today.component.scss',
})
export class IntakeTodayComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  protected readonly intake = inject(IntakeStateService);

  /** Приём, открытый из push-уведомления (/health/intake/dose/:doseId): подсвечивается и прокручивается в вид. */
  readonly doseId = input<string | undefined>(undefined);

  readonly data = signal<IntakeToday | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly subject = signal('all');
  readonly busy = signal<string | null>(null);

  private timer: ReturnType<typeof setInterval> | null = null;
  private scrolledTo: string | null = null;

  protected readonly Outcome = DoseOutcome;
  protected readonly Action = DoseAction;
  protected readonly subjectKey = subjectKey;
  protected readonly subjectName = subjectName;
  protected readonly clock = clock;
  protected readonly formatUnits = formatUnits;

  protected readonly heading = computed(() => {
    const d = this.data();
    return d ? headingDate(d.date) : '';
  });

  protected readonly summary = computed(() => {
    const c = this.data()?.counters;
    if (!c || c.total === 0) return '';
    const parts = [`Принято ${c.taken} из ${c.total}`];
    if (c.nextAt) parts.push(`следующий приём в ${clockOfIso(c.nextAt)}`);
    return parts.join(' · ');
  });

  /** Полоса дня: доля принятого и пропущенного от всех приёмов. */
  protected readonly bar = computed(() => {
    const c = this.data()?.counters;
    if (!c || c.total === 0) return { taken: 0, missed: 0 };
    return { taken: (c.taken / c.total) * 100, missed: (c.missed / c.total) * 100 };
  });

  protected readonly multiPerson = computed(() => (this.data()?.subjects.length ?? 0) > 1);

  /** Активный курс есть хотя бы один — иначе показываем приглашение создать первый. */
  protected readonly hasCourses = computed(() => (this.intake.courses()?.length ?? 0) > 0);

  private readonly allGroups = computed<Group[]>(() => {
    const d = this.data();
    if (!d) return [];
    const focus = this.doseId();
    return PERIODS.map((p) => {
      const rows = d.items.filter((i) => i.period === p.period).map((item) => this.toRow(item, focus));
      return { ...p, rows, isNow: rows.some((r) => r.item.outcome === DoseOutcome.Due) };
    }).filter((g) => g.rows.length > 0);
  });

  /** Крупная карточка «сейчас»: первый наступивший приём, по которому я могу действовать. */
  protected readonly nowRow = computed<Row | null>(() =>
    this.allGroups().flatMap((g) => g.rows).find((r) => r.item.outcome === DoseOutcome.Due && r.item.canAct) ?? null);

  /** Группы без строк, целиком ушедших в карточку «сейчас», не показываем — пустой заголовок сбивает. */
  protected readonly groups = computed<Group[]>(() => {
    const now = this.nowRow();
    return this.allGroups().map((g) => ({ ...g, rows: g.rows.filter((r) => r !== now) })).filter((g) => g.rows.length > 0);
  });

  protected readonly alerts = computed(() => this.data()?.alerts ?? []);
  protected readonly lowStock = computed(() => this.data()?.lowStock ?? []);
  protected readonly week = computed(() => this.data()?.week ?? []);
  protected readonly asNeeded = computed(() => this.data()?.asNeeded ?? []);

  constructor() {
    // Любое изменение курсов/приёмов или смена человека — перечитываем.
    effect(() => {
      this.intake.version();
      this.subject();
      untracked(() => void this.load());
    });
  }

  ngOnInit(): void {
    // Приёмы «наступают» сами — обновляем раз в минуту, пока экран открыт.
    this.timer = setInterval(() => void this.load(true), REFRESH_MS);
  }

  ngOnDestroy(): void {
    if (this.timer !== null) clearInterval(this.timer);
  }

  protected setSubject(key: string): void {
    this.subject.set(key);
  }

  protected async load(silent = false): Promise<void> {
    if (!silent) this.loading.set(true);
    try {
      const subject = this.subject();
      this.data.set(await this.api.getIntakeToday(subject === 'all' ? {} : { subject }));
      this.loadError.set(null);
      this.scrollToFocused();
    } catch {
      if (!silent) this.loadError.set('Не удалось загрузить приёмы. Проверьте соединение и попробуйте ещё раз.');
    } finally {
      this.loading.set(false);
    }
  }

  protected async act(item: TodayDose, action: DoseAction): Promise<void> {
    if (this.busy()) return;
    this.busy.set(this.keyOf(item));
    try {
      await this.api.applyDose(item.courseId, item.scheduledAt, action);
      this.toast.success(action === DoseAction.Taken ? 'Приём отмечен'
        : action === DoseAction.Skip ? 'Приём пропущен' : 'Напомним позже');
      this.intake.changed();
    } catch (e) {
      this.toast.error(e instanceof ApiError && e.message ? e.message : 'Не удалось выполнить действие');
    } finally {
      this.busy.set(null);
    }
  }

  protected async undo(item: TodayDose): Promise<void> {
    if (!item.doseId || this.busy()) return;
    this.busy.set(this.keyOf(item));
    try {
      await this.api.undoDose(item.doseId);
      this.toast.success('Отметка отменена');
      this.intake.changed();
    } catch (e) {
      this.toast.error(e instanceof ApiError && e.message ? e.message : 'Не удалось отменить отметку');
    } finally {
      this.busy.set(null);
    }
  }

  /** «По необходимости»: сверх суточного лимита — сначала подтверждение. */
  protected async takeAsNeeded(prn: TodayAsNeeded): Promise<void> {
    if (this.busy()) return;
    this.busy.set(prn.courseId);
    try {
      await this.api.takeAsNeeded(prn.courseId, false);
      this.toast.success('Приём отмечен');
      this.intake.changed();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409 && e.message.startsWith('Сегодня уже принято')) {
        const ok = await this.confirm.confirm({
          title: 'Превышен суточный лимит',
          message: `${e.message} Всё равно отметить приём?`,
          confirmText: 'Отметить',
        });
        if (ok) {
          try {
            await this.api.takeAsNeeded(prn.courseId, true);
            this.toast.success('Приём отмечен');
            this.intake.changed();
          } catch (e2) {
            this.toast.error(e2 instanceof ApiError && e2.message ? e2.message : 'Не удалось отметить приём');
          }
        }
      } else {
        this.toast.error(e instanceof ApiError && e.message ? e.message : 'Не удалось отметить приём');
      }
    } finally {
      this.busy.set(null);
    }
  }

  /** Состояние квадрата-отметки: принято / пропущено (не отмечено) / осознанно пропущено / ещё открыто. */
  protected boxState(item: TodayDose): string {
    switch (item.outcome) {
      case DoseOutcome.OnTime:
      case DoseOutcome.Late: return "it-box ok";
      case DoseOutcome.Missed: return "it-box missed";
      case DoseOutcome.Skipped: return "it-box skipped";
      default: return "it-box open";
    }
  }

  protected isBusy(item: TodayDose): boolean {
    return this.busy() === this.keyOf(item);
  }

  protected stockBar(low: LowStock): number {
    return Math.max(6, Math.min(100, (low.daysCovered / Math.max(1, low.lowStockDays)) * 60));
  }

  protected stockCaption(low: LowStock): string {
    const left = low.courseDaysLeft === null ? '' : ` · курс ещё ${low.courseDaysLeft} дн.`;
    return `${coversText(low.daysCovered)}${left}`;
  }

  protected weekSegments(d: WeekDay): { cls: string; n: number }[] {
    return [
      { cls: 'missed', n: d.missed },
      { cls: 'late', n: d.late },
      { cls: 'ok', n: d.onTime },
      { cls: 'skipped', n: d.skipped },
      { cls: 'upcoming', n: d.upcoming },
    ].filter((s) => s.n > 0);
  }

  protected weekday(d: WeekDay): string {
    return shortDate(d.date).split(',')[0];
  }

  protected person(s: IntakeSubject): { key: string; firstName: string } {
    return { key: s.id, firstName: s.name };
  }

  private keyOf(item: TodayDose): string {
    return `${item.courseId}:${item.scheduledAt}`;
  }

  private toRow(item: TodayDose, focus: string | undefined): Row {
    const menu: ActionMenuItem[] = [];
    if (item.canAct) {
      if (item.outcome === DoseOutcome.Due || item.outcome === DoseOutcome.Upcoming) {
        menu.push({ label: 'Отложить на 30 минут', icon: 'ph ph-clock-clockwise', handler: () => void this.act(item, DoseAction.Snooze30) });
        menu.push({ label: 'Пропустить', icon: 'ph ph-x', handler: () => void this.act(item, DoseAction.Skip) });
      }
      if (item.outcome === DoseOutcome.Missed) {
        menu.push({ label: 'Пропустить', icon: 'ph ph-x', handler: () => void this.act(item, DoseAction.Skip) });
      }
      if ((item.outcome === DoseOutcome.OnTime || item.outcome === DoseOutcome.Late || item.outcome === DoseOutcome.Skipped) && item.doseId) {
        menu.push({ label: 'Отменить отметку', icon: 'ph ph-arrow-counter-clockwise', handler: () => void this.undo(item) });
      }
    }
    return {
      item,
      sub: this.subText(item),
      time: clock(item.localTime),
      actions: menu,
      highlighted: !!focus && item.doseId === focus,
    };
  }

  private subText(item: TodayDose): string {
    const parts = [formatUnits(item.units, item.unit)];
    const food = foodText(item.food);
    if (food) parts.push(food);
    switch (item.outcome) {
      case DoseOutcome.OnTime:
      case DoseOutcome.Late:
        parts.push(item.takenAt ? `принято в ${clockOfIso(item.takenAt)}${item.outcome === DoseOutcome.Late ? ' (с опозданием)' : ''}` : 'принято');
        break;
      case DoseOutcome.Skipped:
        parts.push('пропущен');
        break;
      case DoseOutcome.Missed:
        return `Не отмечено · прошло ${agoText(item.scheduledAt)}${item.isWatching || !item.subject.isSelf ? ' · вы получили уведомление' : ''}`;
      default:
        if (item.snoozedUntil && new Date(item.snoozedUntil).getTime() > Date.now())
          parts.push(`отложено до ${clockOfIso(item.snoozedUntil)}`);
        else if (item.totalDays !== null || item.dayNumber > 0) parts.push(progressText(item.dayNumber, item.totalDays));
    }
    return parts.join(' · ');
  }

  private scrollToFocused(): void {
    const id = this.doseId();
    if (!id || this.scrolledTo === id) return;
    setTimeout(() => {
      const el = document.getElementById(`dose-${id}`);
      if (el) {
        this.scrolledTo = id;
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
      }
    }, 50);
  }
}
