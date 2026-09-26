import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ApiService } from '../../services/api.service';
import { BreakpointService } from '../../services/breakpoint.service';
import { ActionMenuComponent, ActionMenuItem } from '../../shared/action-menu/action-menu.component';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';
import { SidePanelComponent } from '../../shared/side-panel/side-panel.component';
import { ToastService } from '../../shared/toast/toast.service';
import { TrendLineComponent } from '../../shared/trend-line/trend-line.component';
import { pluralizeRu } from '../../shared/util/pluralize';
import {
  HealthMetricDefinition, HealthNote, HealthNoteCatalog, HealthNoteKind,
} from '../../models/types';
import {
  HEALTH_KINDS, NoteDescription, dayHeading, dayKey, describeNote, formatClock, formatNumber,
  kindMeta, metricValueText, wellbeingLevel,
} from '../../shared/util/health-note-labels';
import { HealthNoteFormComponent } from '../health-note-form/health-note-form.component';

const DAY_MS = 86_400_000;
const PAGE_DAYS = 30;

interface FeedItem {
  note: HealthNote;
  desc: NoteDescription;
  time: string;
  icon: string;
  color: string;
  flagged: boolean;
  actions: ActionMenuItem[];
}

interface FeedGroup {
  key: string;
  title: string;
  subtitle: string;
  items: FeedItem[];
}

interface MetricTile {
  code: string;
  name: string;
  icon: string;
  value: string;
  unit: string;
  stamp: string;
  values: number[];
  band: [number, number] | null;
  caption: string;
}

interface WellbeingTile {
  value: string;
  bars: { height: number; score: number }[];
  caption: string;
}

/** Медиана/квантиль по отсортированной копии — для полосы «обычного диапазона» пользователя. */
function quantile(sorted: number[], q: number): number {
  const pos = (sorted.length - 1) * q;
  const lo = Math.floor(pos);
  const hi = Math.ceil(pos);
  return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
}

/**
 * Страница «Дневник» (вкладка хаба «Здоровье», макет «Screen - Diary»): сводка замеров с
 * трендом, лента по дням с фильтром по типу, форма записи в боковой панели (десктоп) или
 * нижнем листе (мобайл). Дневник строго личный — видите только вы, в семью не шарится.
 */
@Component({
  selector: 'app-health-notes-tab',
  imports: [
    ActionMenuComponent, BottomSheetComponent, HealthNoteFormComponent, SearchFieldComponent,
    SidePanelComponent, TrendLineComponent,
  ],
  templateUrl: './health-notes-tab.component.html',
  styleUrl: './health-notes-tab.component.scss',
})
export class HealthNotesTabComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly breakpoints = inject(BreakpointService);

  protected readonly kinds = HEALTH_KINDS;

  readonly notes = signal<HealthNote[]>([]);
  readonly catalog = signal<HealthNoteCatalog | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  /** Глубина загрузки в днях; «Показать ещё» расширяет на 30. */
  readonly days = signal(PAGE_DAYS);
  readonly filter = signal<HealthNoteKind | null>(null);
  readonly query = signal('');

  readonly formOpen = signal(false);
  readonly editing = signal<HealthNote | null>(null);

  protected readonly isWide = computed(() => this.breakpoints.tier() === 'wide');
  private readonly metrics = computed<HealthMetricDefinition[]>(() => this.catalog()?.metrics ?? []);

  protected readonly countLabel = computed(() => {
    const n = this.notes().length;
    return `${n} ${pluralizeRu(n, 'запись', 'записи', 'записей')} за ${this.days()} дней`;
  });

  /** Лента: фильтр по типу и поиск (на клиенте — поля зашифрованы), группировка по локальным дням. */
  protected readonly groups = computed<FeedGroup[]>(() => {
    const kind = this.filter();
    const q = this.query().trim().toLowerCase();
    const metrics = this.metrics();
    const byDay = new Map<string, FeedItem[]>();

    for (const note of this.notes()) {
      if (kind !== null && note.kind !== kind) continue;
      const desc = describeNote(note, metrics);
      if (q && !`${desc.title} ${desc.subtitle} ${note.text ?? ''}`.toLowerCase().includes(q)) continue;
      const meta = kindMeta(note.kind);
      const item: FeedItem = {
        note,
        desc,
        time: formatClock(note.occurredAt),
        icon: meta.icon,
        color: meta.color,
        flagged: note.includeInDoctorQuestions,
        actions: [
          { label: 'Изменить', icon: 'ph ph-pencil-simple', handler: () => this.openForm(note) },
          { label: 'Удалить', icon: 'ph ph-trash', danger: true, handler: () => void this.remove(note) },
        ],
      };
      const key = dayKey(note.occurredAt);
      byDay.set(key, [...(byDay.get(key) ?? []), item]);
    }

    return [...byDay.entries()]
      .sort(([a], [b]) => (a < b ? 1 : -1))
      .map(([key, items]) => ({ key, ...dayHeading(key), items }));
  });

  protected readonly tiles = computed<MetricTile[]>(() =>
    [
      this.metricTile('blood_pressure', 'heartbeat', 7),
      this.metricTile('pulse', 'wave-sine', 7),
      this.metricTile('weight', 'scales', 30),
    ].filter((t): t is MetricTile => t !== null));

  protected readonly wellbeingTile = computed<WellbeingTile | null>(() => this.buildWellbeingTile());

  ngOnInit(): void {
    void this.load();
    void this.loadCatalog();
  }

  protected setFilter(kind: HealthNoteKind | null): void {
    this.filter.set(kind);
  }

  protected openForm(note: HealthNote | null = null): void {
    this.editing.set(note);
    this.formOpen.set(true);
  }

  protected closeForm(): void {
    this.formOpen.set(false);
    this.editing.set(null);
  }

  protected onSaved(): void {
    this.closeForm();
    void this.load();
  }

  protected showOlder(): void {
    this.days.update((d) => d + PAGE_DAYS);
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const from = new Date(Date.now() - this.days() * DAY_MS).toISOString();
      this.notes.set(await this.api.getHealthNotes({ from }));
    } catch {
      this.loadError.set('Не удалось загрузить дневник. Проверьте соединение и попробуйте ещё раз.');
    } finally {
      this.loading.set(false);
    }
  }

  private async loadCatalog(): Promise<void> {
    try {
      this.catalog.set(await this.api.getHealthNoteCatalog());
    } catch {
      // Без каталога лента всё равно работает (замеры покажутся по коду), а форма — без чипов.
      this.toast.error('Не удалось загрузить справочник замеров');
    }
  }

  private async remove(note: HealthNote): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Удалить запись?',
      message: `«${describeNote(note, this.metrics()).title}» будет удалена без возможности восстановления.`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;
    try {
      await this.api.deleteHealthNote(note.id);
      this.notes.update((list) => list.filter((n) => n.id !== note.id));
      this.toast.success('Запись удалена');
    } catch {
      this.toast.error('Не удалось удалить запись');
    }
  }

  // ---- Сводка ----

  private series(code: string, days: number): { at: string; v: number }[] {
    const since = Date.now() - days * DAY_MS;
    return this.notes()
      .filter((n) => n.kind === HealthNoteKind.Metric && n.metric?.code === code && new Date(n.occurredAt).getTime() >= since)
      .sort((a, b) => a.occurredAt.localeCompare(b.occurredAt))
      .map((n) => ({ at: n.occurredAt, v: n.metric!.value }));
  }

  private stamp(iso: string): string {
    const h = dayHeading(dayKey(iso));
    return h.title === 'Сегодня' || h.title === 'Вчера'
      ? `${h.title.toLowerCase()} ${formatClock(iso)}`
      : h.title;
  }

  private metricTile(code: string, icon: string, windowDays: number): MetricTile | null {
    const def = this.metrics().find((m) => m.code === code);
    const month = this.series(code, 30);
    const latestNote = this.notes()
      .filter((n) => n.kind === HealthNoteKind.Metric && n.metric?.code === code)
      .sort((a, b) => b.occurredAt.localeCompare(a.occurredAt))[0];
    if (!latestNote?.metric) return null;

    const window = this.series(code, windowDays).map((p) => p.v);
    const sortedMonth = month.map((p) => p.v).sort((a, b) => a - b);
    // Полоса — обычный диапазон по истории самого пользователя (не медицинская норма).
    const band: [number, number] | null = sortedMonth.length >= 5
      ? [quantile(sortedMonth, 0.25), quantile(sortedMonth, 0.75)]
      : null;
    const latest = latestNote.metric.value;

    let caption: string;
    if (code === 'weight') {
      const first = month[0]?.v;
      const delta = first === undefined ? 0 : Math.round((latest - first) * 10) / 10;
      caption = month.length < 2 || delta === 0
        ? '30 дней · без изменений'
        : `30 дней · ${delta > 0 ? '+' : '−'}${formatNumber(Math.abs(delta))} кг`;
    } else if (!band || window.length < 3) {
      caption = `${windowDays} дней · мало данных для тренда`;
    } else if (latest > band[1]) {
      caption = `${windowDays} дней · выше обычного`;
    } else if (latest < band[0]) {
      caption = `${windowDays} дней · ниже обычного`;
    } else {
      caption = `${windowDays} дней · в вашем обычном диапазоне`;
    }

    return {
      code,
      name: def?.name ?? code,
      icon,
      value: metricValueText(latest, latestNote.metric.value2),
      unit: def?.unit ?? '',
      stamp: this.stamp(latestNote.occurredAt),
      values: window,
      band: code === 'weight' ? null : band,
      caption,
    };
  }

  private buildWellbeingTile(): WellbeingTile | null {
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const perDay = new Map<string, number[]>();
    for (const n of this.notes()) {
      if (n.kind !== HealthNoteKind.Wellbeing || !n.wellbeing) continue;
      const key = dayKey(n.occurredAt);
      perDay.set(key, [...(perDay.get(key) ?? []), n.wellbeing.score]);
    }

    const bars: WellbeingTile['bars'] = [];
    const scores: number[] = [];
    let worst: { score: number; weekday: string } | null = null;
    for (let i = 6; i >= 0; i--) {
      const d = new Date(today.getTime() - i * DAY_MS);
      const key = dayKey(d.toISOString());
      const list = perDay.get(key);
      if (!list) {
        bars.push({ height: 0, score: 0 });
        continue;
      }
      const avg = list.reduce((s, x) => s + x, 0) / list.length;
      scores.push(...list);
      bars.push({ height: Math.round((avg / 5) * 100), score: Math.round(avg) });
      if (!worst || avg < worst.score) worst = { score: avg, weekday: dayHeading(key).subtitle.split(',')[0] };
    }
    if (scores.length === 0) return null;

    const mean = scores.reduce((s, x) => s + x, 0) / scores.length;
    return {
      value: wellbeingLevel(Math.round(mean)).label,
      bars,
      caption: worst && worst.score <= 2 ? `самый тяжёлый день — ${worst.weekday}` : 'стабильно',
    };
  }
}
