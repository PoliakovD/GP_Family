import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminApiService, AdminRelatedAnalyteMatch, KbAnalyteListItem } from '../../../services/admin-api.service';
import { ToastService } from '../../../shared/toast/toast.service';

const RELATED_SEARCH_DEBOUNCE_MS = 300;

export type PayloadEditorSchema = 'lab-analyte' | 'medication';

/** Одна строка refRanges (см. FamilyHub.Modules.Medical.Extraction.LabAnalyteKbPayload.Build на
 * бэкенде — строковые значения enum'ов должны совпадать буква в букву с SexToString/
 * NormKindToString/PopulationToString). sourceDomain/sourceRank обычно проставлены детерминированным
 * merge'ем (ReferenceRangeMerger), не моделью — редактируются здесь только для ручных диапазонов,
 * добавленных администратором с нуля. */
interface RefRangeRow {
  ageFrom: number | null;
  ageTo: number | null;
  sex: '' | 'male' | 'female';
  low: number | null;
  high: number | null;
  unit: string;
  normKind: 'fixed' | 'calculated' | 'qualitative';
  population: 'general' | 'pregnancy' | 'children' | 'cyclePhase';
  populationDetail: string;
  sourceDomain: string;
  sourceRank: number;
}

/** Событие «Сохранить» — changedKeys=null означает режим JSON (лочит payload целиком, прежнее
 * поведение), changedKeys=string[] — режим формы (лочит только "payload.&lt;key&gt;" по каждому
 * реально изменённому полю, см. AdminKbEditRequest.lockedPayloadKeys на бэкенде). */
export interface PayloadSaveEvent {
  payloadJson: string;
  changedKeys: string[] | null;
}

const LAB_ANALYTE_SIMPLE_KEYS = [
  'loincCode', 'defaultUnit', 'plainExplanation', 'whyMeasured', 'highMeans', 'lowMeans', 'calculationInstructions',
] as const;
const LAB_ANALYTE_KNOWN_KEYS = new Set<string>([...LAB_ANALYTE_SIMPLE_KEYS, 'schemaVersion', 'relatedNames', 'refRanges']);

const MEDICATION_SIMPLE_KEYS = [
  'internationalName', 'form', 'purpose', 'simplePurpose', 'usage', 'storage', 'driving', 'specialNotes',
] as const;
const MEDICATION_KNOWN_KEYS = new Set<string>([...MEDICATION_SIMPLE_KEYS, 'schemaVersion', 'tradeNames']);

function asString(v: unknown): string {
  return typeof v === 'string' ? v : '';
}
function asStringArray(v: unknown): string[] {
  return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : [];
}
function asNumber(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}
function splitList(raw: string): string[] {
  return raw.split(',').map((s) => s.trim()).filter((s) => s.length > 0);
}
function orNull(s: string): string | null {
  const trimmed = s.trim();
  return trimmed.length > 0 ? trimmed : null;
}
function asRefRanges(v: unknown): RefRangeRow[] {
  if (!Array.isArray(v)) return [];
  return v.map((item) => {
    const o = (item ?? {}) as Record<string, unknown>;
    const sex = o['sex'];
    const normKind = o['normKind'];
    const population = o['population'];
    return {
      ageFrom: asNumber(o['ageFrom']),
      ageTo: asNumber(o['ageTo']),
      sex: sex === 'male' || sex === 'female' ? sex : '',
      low: asNumber(o['low']),
      high: asNumber(o['high']),
      unit: asString(o['unit']),
      normKind: normKind === 'calculated' || normKind === 'qualitative' ? normKind : 'fixed',
      population:
        population === 'pregnancy' || population === 'children' || population === 'cyclePhase' ? population : 'general',
      populationDetail: asString(o['populationDetail']),
      sourceDomain: asString(o['sourceDomain']),
      sourceRank: asNumber(o['sourceRank']) ?? 0,
    };
  });
}
function refRangeToJson(r: RefRangeRow) {
  return {
    ageFrom: r.ageFrom, ageTo: r.ageTo, sex: r.sex || null, low: r.low, high: r.high, unit: orNull(r.unit),
    normKind: r.normKind, population: r.population, populationDetail: orNull(r.populationDetail),
    sourceDomain: orNull(r.sourceDomain), sourceRank: r.sourceRank,
  };
}
const EMPTY_REF_RANGE = (): RefRangeRow => ({
  ageFrom: null, ageTo: null, sex: '', low: null, high: null, unit: '',
  normKind: 'fixed', population: 'general', populationDetail: '', sourceDomain: '', sourceRank: 0,
});

/**
 * Редактор payload справочника — «Форма ⇄ JSON» (§10 плана). Заменяет два одинаковых
 * `<textarea>` в admin-catalog (показатели/медикаменты): форма — поля + таблица refRanges
 * (только для показателей) со значениями строго из схемы LabAnalyteKbPayload.Build/KbWriter;
 * JSON остаётся источником истины и путём для всего, чего форма не покрывает.
 *
 * Инварианты (без них форма опаснее textarea, см. план §10):
 * - schemaVersion и любые НЕИЗВЕСТНЫЕ ключи сохраняются дословно — держим их в parsedRoot и
 *   подмешиваем обратно при сериализации (см. saveFromForm), не пересобираем объект с нуля.
 * - Переключение форма→JSON сериализует ТЕКУЩЕЕ состояние формы (включая ещё не сохранённые
 *   правки), не последний payloadJson с сервера — иначе набранное в форме терялось бы молча.
 * - Невалидный JSON блокирует переключение JSON→форма (остаёмся в JSON с явной ошибкой), а не
 *   тихо теряет текст.
 */
@Component({
    selector: 'app-admin-payload-editor',
    imports: [FormsModule],
    templateUrl: './admin-payload-editor.component.html'
})
export class AdminPayloadEditorComponent implements OnChanges {
  @Input({ required: true }) schema!: PayloadEditorSchema;
  @Input() payloadJson = '{}';
  @Input() busy = false;
  /** Id текущей открытой статьи — исключается из результатов поиска пикера «Что смотрят
   * вместе» (показатель не может ссылаться сам на себя) и из состава её собственных чипов при
   * желании админа так поступить, но это уже его решение — здесь только фильтр поиска. */
  @Input() excludeId: string | null = null;
  @Output() readonly save = new EventEmitter<PayloadSaveEvent>();
  /** Клик по уже резолвленному чипу «Что смотрят вместе» — родитель (admin-catalog) открывает
   * ту статью, переключая текущий выбор в списке показателей. */
  @Output() readonly openRelated = new EventEmitter<{ id: string; displayName: string }>();

  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);

  readonly mode = signal<'form' | 'json'>('form');
  readonly jsonDraft = signal('{}');
  readonly jsonError = signal<string | null>(null);

  // --- Показатели ---
  readonly loincCode = signal('');
  readonly defaultUnit = signal('');
  readonly plainExplanation = signal('');
  readonly whyMeasured = signal('');
  readonly highMeans = signal('');
  readonly lowMeans = signal('');
  readonly calculationInstructions = signal('');
  /** «Что смотрят вместе» — пикер, не свободный текст (см. class doc): каждое имя добавляется
   * ТОЛЬКО через поиск по справочнику, чтобы гарантированно резолвиться в реальную статью, а не
   * быть опечаткой/оборванной ссылкой. relatedMatches — кэш резолва по точному имени (id/специмен
   * для disambiguation и кликабельной ссылки), ключ — само имя. */
  readonly relatedNames = signal<string[]>([]);
  readonly relatedMatches = signal<Record<string, AdminRelatedAnalyteMatch>>({});
  readonly relatedSearchQuery = signal('');
  readonly relatedSearchResults = signal<KbAnalyteListItem[]>([]);
  readonly relatedSearchLoading = signal(false);
  private relatedSearchDebounce?: ReturnType<typeof setTimeout>;
  readonly refRanges = signal<RefRangeRow[]>([]);

  // --- Медикаменты ---
  readonly internationalName = signal('');
  readonly tradeNames = signal('');
  readonly form = signal('');
  readonly purpose = signal('');
  readonly simplePurpose = signal('');
  readonly usage = signal('');
  readonly storage = signal('');
  readonly driving = signal('');
  readonly specialNotes = signal('');

  private parsedRoot: Record<string, unknown> = {};
  private changedKeys = new Set<string>();

  get knownKeys(): Set<string> {
    return this.schema === 'lab-analyte' ? LAB_ANALYTE_KNOWN_KEYS : MEDICATION_KNOWN_KEYS;
  }

  get unknownKeyCount(): number {
    return Object.keys(this.parsedRoot).filter((k) => !this.knownKeys.has(k)).length;
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['payloadJson'] || changes['schema']) {
      this.mode.set('form');
      this.jsonError.set(null);
      this.changedKeys = new Set();
      let parsed: unknown;
      try {
        parsed = JSON.parse(this.payloadJson);
      } catch {
        parsed = {};
      }
      this.parsedRoot = (parsed && typeof parsed === 'object' ? parsed : {}) as Record<string, unknown>;
      this.applyFromRoot();
      this.jsonDraft.set(this.prettyJson(this.parsedRoot));
    }
  }

  private applyFromRoot(): void {
    const r = this.parsedRoot;
    if (this.schema === 'lab-analyte') {
      this.loincCode.set(asString(r['loincCode']));
      this.defaultUnit.set(asString(r['defaultUnit']));
      this.plainExplanation.set(asString(r['plainExplanation']));
      this.whyMeasured.set(asString(r['whyMeasured']));
      this.highMeans.set(asString(r['highMeans']));
      this.lowMeans.set(asString(r['lowMeans']));
      this.calculationInstructions.set(asString(r['calculationInstructions']));
      this.relatedNames.set(asStringArray(r['relatedNames']));
      this.relatedMatches.set({});
      void this.resolveRelated();
      this.refRanges.set(asRefRanges(r['refRanges']));
    } else {
      this.internationalName.set(asString(r['internationalName']));
      this.tradeNames.set(asStringArray(r['tradeNames']).join(', '));
      this.form.set(asString(r['form']));
      this.purpose.set(asString(r['purpose']));
      this.simplePurpose.set(asString(r['simplePurpose']));
      this.usage.set(asString(r['usage']));
      this.storage.set(asString(r['storage']));
      this.driving.set(asString(r['driving']));
      this.specialNotes.set(asString(r['specialNotes']));
    }
  }

  private buildObjectFromForm(): Record<string, unknown> {
    const next = { ...this.parsedRoot };
    if (this.schema === 'lab-analyte') {
      next['loincCode'] = orNull(this.loincCode());
      next['defaultUnit'] = orNull(this.defaultUnit());
      next['plainExplanation'] = orNull(this.plainExplanation());
      next['whyMeasured'] = orNull(this.whyMeasured());
      next['highMeans'] = orNull(this.highMeans());
      next['lowMeans'] = orNull(this.lowMeans());
      next['calculationInstructions'] = orNull(this.calculationInstructions());
      next['relatedNames'] = this.relatedNames();
      next['refRanges'] = this.refRanges().map(refRangeToJson);
    } else {
      next['internationalName'] = orNull(this.internationalName());
      next['tradeNames'] = splitList(this.tradeNames());
      next['form'] = orNull(this.form());
      next['purpose'] = orNull(this.purpose());
      next['simplePurpose'] = orNull(this.simplePurpose());
      next['usage'] = orNull(this.usage());
      next['storage'] = orNull(this.storage());
      next['driving'] = orNull(this.driving());
      next['specialNotes'] = orNull(this.specialNotes());
    }
    return next;
  }

  private prettyJson(obj: unknown): string {
    try {
      return JSON.stringify(obj, null, 2);
    } catch {
      return '{}';
    }
  }

  markChanged(key: string): void {
    this.changedKeys.add(key);
  }

  // --- «Что смотрят вместе» — пикер по справочнику, не свободный текст ---

  /** Резолвит уже сохранённые related-имена в реальные строки справочника — best-effort, ошибка
   * сети не должна ломать редактор, просто чипы останутся без бейджа/ссылки до следующей попытки. */
  private async resolveRelated(): Promise<void> {
    const names = this.relatedNames();
    if (names.length === 0) {
      this.relatedMatches.set({});
      return;
    }
    try {
      const matches = await this.api.resolveRelatedAnalytes(names);
      const map: Record<string, AdminRelatedAnalyteMatch> = {};
      for (const m of matches) map[m.name] = m;
      this.relatedMatches.set(map);
    } catch {
      // Не критично — чипы отрисуются без резолва, форма остаётся рабочей.
    }
  }

  onRelatedSearchInput(value: string): void {
    this.relatedSearchQuery.set(value);
    clearTimeout(this.relatedSearchDebounce);
    if (!value.trim()) {
      this.relatedSearchResults.set([]);
      return;
    }
    this.relatedSearchDebounce = setTimeout(() => void this.runRelatedSearch(value), RELATED_SEARCH_DEBOUNCE_MS);
  }

  private async runRelatedSearch(query: string): Promise<void> {
    this.relatedSearchLoading.set(true);
    try {
      const page = await this.api.searchLabAnalytes(query, 0, 8);
      const already = new Set(this.relatedNames());
      this.relatedSearchResults.set(
        page.items.filter((item) => item.id !== this.excludeId && !already.has(item.displayName)),
      );
    } catch {
      this.toast.error('Не удалось найти показатели.');
      this.relatedSearchResults.set([]);
    } finally {
      this.relatedSearchLoading.set(false);
    }
  }

  pickRelated(item: KbAnalyteListItem): void {
    this.markChanged('relatedNames');
    this.relatedNames.update((names) => [...names, item.displayName]);
    this.relatedMatches.update((map) => ({
      ...map,
      [item.displayName]: {
        name: item.displayName, id: item.id, displayName: item.displayName, specimenDisplayName: item.specimenDisplayName,
      },
    }));
    this.relatedSearchQuery.set('');
    this.relatedSearchResults.set([]);
  }

  removeRelated(name: string): void {
    this.markChanged('relatedNames');
    this.relatedNames.update((names) => names.filter((n) => n !== name));
  }

  /** null для имени, ещё не резолвленного (запрос в процессе) или без совпадения в справочнике —
   * шаблон показывает чип без ссылки/бейджа в обоих случаях, разница не критична для UX. */
  relatedMatchFor(name: string): AdminRelatedAnalyteMatch | null {
    return this.relatedMatches()[name] ?? null;
  }

  openRelatedChip(name: string): void {
    const match = this.relatedMatchFor(name);
    if (match?.id && match.displayName) this.openRelated.emit({ id: match.id, displayName: match.displayName });
  }

  addRefRange(): void {
    this.markChanged('refRanges');
    this.refRanges.update((rows) => [...rows, EMPTY_REF_RANGE()]);
  }

  duplicateRefRange(index: number): void {
    this.markChanged('refRanges');
    this.refRanges.update((rows) => {
      const copy = [...rows];
      copy.splice(index + 1, 0, { ...rows[index] });
      return copy;
    });
  }

  removeRefRange(index: number): void {
    this.markChanged('refRanges');
    this.refRanges.update((rows) => rows.filter((_, i) => i !== index));
  }

  updateRefRange<K extends keyof RefRangeRow>(index: number, key: K, value: RefRangeRow[K]): void {
    this.markChanged('refRanges');
    this.refRanges.update((rows) => rows.map((row, i) => (i === index ? { ...row, [key]: value } : row)));
  }

  setMode(next: 'form' | 'json'): void {
    if (next === this.mode()) return;

    if (next === 'json') {
      // Сериализуем ТЕКУЩЕЕ состояние формы (в т.ч. ещё не сохранённые правки), не последний
      // payloadJson с сервера — иначе набранное в форме терялось бы при простом просмотре JSON.
      this.jsonDraft.set(this.prettyJson(this.buildObjectFromForm()));
      this.jsonError.set(null);
      this.mode.set('json');
      return;
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(this.jsonDraft());
    } catch (e) {
      this.jsonError.set(`Невалидный JSON — нельзя переключиться в форму: ${(e as Error).message}`);
      return;
    }
    if (!parsed || typeof parsed !== 'object') {
      this.jsonError.set('JSON должен быть объектом.');
      return;
    }

    this.parsedRoot = parsed as Record<string, unknown>;
    this.applyFromRoot();
    // JSON-режим не отслеживает, какие поля именно менялись — считаем изменённым любое известное
    // поле формы, чтобы обратное переключение форма→JSON→форма не потеряло правки молча.
    this.changedKeys = new Set(this.knownKeys);
    this.jsonError.set(null);
    this.mode.set('form');
  }

  saveFromForm(): void {
    const payloadJson = JSON.stringify(this.buildObjectFromForm());
    this.save.emit({ payloadJson, changedKeys: [...this.changedKeys] });
  }

  saveFromJson(): void {
    try {
      JSON.parse(this.jsonDraft());
    } catch (e) {
      this.jsonError.set(`Невалидный JSON: ${(e as Error).message}`);
      return;
    }
    this.jsonError.set(null);
    this.save.emit({ payloadJson: this.jsonDraft(), changedKeys: null });
  }
}
