import { Component, ElementRef, HostListener, OnDestroy, OnInit, ViewChild, effect, inject, input } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { ToastService } from '../../shared/toast/toast.service';
import { OverlayStackService } from '../../shared/util/overlay-stack.service';
import { ApiService, ApiError } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import { AuthService } from '../../services/auth.service';
import { PageActionService } from '../../services/page-action.service';
import { AiStatusService } from '../../services/ai-status.service';
import { BreakpointService } from '../../services/breakpoint.service';
import {
  ExtractionJobStatus, ExtractionStatus, IndicatorFlag, MedicalRecordKind, RefSource,
} from '../../models/types';
import type {
  ExtractionStatusResponse,
  IndicatorDto,
  IndicatorHistoryPoint,
  KbAnalyteCard,
  KbMedicationCard,
  MedicalRecord,
  MedicalRecordFilter,
  PatientContextDto,
  RecordSummaryResponse,
  UpdateIndicatorRequest,
  VisitConclusion,
} from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { PipelineProgressComponent, PipelineStep } from '../../shared/pipeline-progress/pipeline-progress.component';
import { KbCardComponent } from '../kb-card/kb-card.component';
import { AvatarComponent } from '../../shared/avatar/avatar.component';
import { PersonChipComponent } from '../../shared/person-chip/person-chip.component';
import { BackLinkComponent } from '../../shared/back-link/back-link.component';
import { ActionMenuComponent, type ActionMenuItem } from '../../shared/action-menu/action-menu.component';
import { InfiniteScrollSentinelComponent } from '../../shared/infinite-scroll-sentinel/infinite-scroll-sentinel.component';
import { IndicatorInfoComponent, type IndicatorInfoReading } from '../indicator-info/indicator-info.component';
import { IndicatorInfoPanelComponent } from '../indicator-info/indicator-info-panel.component';
import { AttachmentListComponent } from '../../shared/attachment-list/attachment-list.component';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { shortenDisplayName, shortenDoctorName, personAvatarPartsFromName } from '../../shared/util/person-name';
import { pluralizeRu } from '../../shared/util/pluralize';
import { formatDayMonth, formatDayMonthYear, formatYear } from '../../shared/util/date-format';
import { buildPatientOptions, type PatientOption } from '../../shared/util/patient-options';
import { MEDICAL_RECORD_KIND_LABELS, medicalRecordKindBasePath, type MedicalRecordKindLabels } from '../../shared/util/medical-record-labels';
import { enrichmentFollowupStep, nextPipelineSteps } from './extraction-pipeline';
import { ExtractionPoller } from './extraction-poller';
import { accessSummary } from './record-access';
import { RecordAccessSheetComponent } from './record-access-sheet.component';
import { RecordEditSheetComponent } from './record-edit-sheet.component';
import { indicatorLabel, specimenLabelFor } from './indicator-display';
import { emptyIndicatorForm, sanitizeIndicatorForm } from './indicator-form';
import { IndicatorTableComponent } from './indicator-table.component';


const EXTRACTION_POLL_INTERVAL_MS = 1500;
/** Опрос статуса задачи, пока она ждёт возвращения ИИ (LM Studio) — реже обычного. */
const WAITING_POLL_INTERVAL_MS = 10_000;
/** Подряд неудачных опросов статуса, после которых поллинг реально останавливается — 5 × 1.5с ≈
 * 7.5с непрерывных сбоев переживает короткий блип сети/фоновую вкладку, не маскирует настоящий
 * обрыв связи навсегда. См. extraction-poller.ts. */
const MAX_CONSECUTIVE_POLL_FAILURES = 5;
const SEARCH_DEBOUNCE_MS = 300;
/** Сколько ждать после Completed, прежде чем убрать живой прогресс — успевает мигнуть галочка
 * «Готово», не исчезает мгновенно. */
const PIPELINE_CLEAR_DELAY_MS = 2500;
/** §5 плана «живой конвейер» — опрос показателей ЗАПИСИ, пока хотя бы один из них
 * enrichmentPending (обогащение справочника ещё не завершилось); реже, чем EXTRACTION_POLL_
 * INTERVAL_MS выше — это фоновый процесс, который может идти минутами, не секундами. */
const ENRICHMENT_POLL_INTERVAL_MS = 5000;
/** Опрос резюме после ручной правки: бэк ждёт 15 с дебаунса, потом идёт LLM — 4 с × 45 ≈ 3 мин. */
const SUMMARY_POLL_INTERVAL_MS = 4000;
const SUMMARY_POLL_MAX_ATTEMPTS = 45;

/** Токен для GET /api/medical-records?kind=. */
const LIST_KIND_TOKEN: Record<MedicalRecordKind, 'analysis' | 'visit'> = {
  [MedicalRecordKind.Analysis]: 'analysis',
  [MedicalRecordKind.DoctorVisit]: 'visit',
};

/** Группа записей одного человека (редизайн v2, PR3b) — ключ тот же, что у PatientOption
 * (shared/util/patient-options.ts). */
interface RecordGroup {
  key: string;
  personName: string;
  records: MedicalRecord[];
}

let nextInstanceId = 0;

/**
 * Panel (не Page — своего URL нет, контекст приходит через input): список записей одного вида
 * (анализ/посещение врача — MedicalRecordKind), форма создания, серверные фильтры + пагинация,
 * шторка «Доступ», вложения. Переиспользуется двумя тонкими Page-обёртками: medical-records-tab
 * («Анализы») и doctor-visits-tab («Врачи») — см. .claude/patterns/frontend_web.md про таксономию
 * Page/Panel.
 *
 * UX-редизайн: форма создания скрыта за «+ Добавить» (была всегда развёрнута над списком),
 * список серверно фильтруется/пагинируется (было — голый список без сортировки), live-прогресс
 * распознавания вместо статичной строки.
 */
@Component({
    selector: 'app-medical-records-panel',
    imports: [
        NgTemplateOutlet,
        FormsModule, LoadingSpinnerComponent, BottomSheetComponent,
        PipelineProgressComponent, KbCardComponent,
        AvatarComponent, PersonChipComponent, BackLinkComponent, ActionMenuComponent, InfiniteScrollSentinelComponent,
        IndicatorInfoComponent, IndicatorInfoPanelComponent,
        AttachmentListComponent, RouterLink, RecordAccessSheetComponent, RecordEditSheetComponent, IndicatorTableComponent,
    ],
    templateUrl: './medical-records-panel.component.html',
    styleUrl: './medical-records-panel.component.scss'
})
export class MedicalRecordsPanelComponent implements OnInit, OnDestroy {
  readonly kind = input.required<MedicalRecordKind>();

  /** Редизайн v3 — «одиночный режим» (PR6 плана): мобильный экран открытой записи
   * (RecordDetailPageComponent) передаёт сюда id вместо списка — та же панель, но без
   * пагинации/группировки/фильтров, с одной всегда развёрнутой карточкой. См. refresh()/шаблон
   * (ветка @if (recordId())) и recordCard-ng-template (singleMode). */
  readonly recordId = input<string | null>(null);
  /** ?firstReview=1 (PR7) — сразу после сохранения в record-add.component.ts, показывает
   * одноразовый баннер-подсказку над карточкой; не персистится (не добавляется обратно). */
  readonly firstReview = input<string | null>(null);

  /** ?person=me — из плитки хаба «Здоровье» (редизайн навигации): раздел сразу отфильтрован на
   * себя (тот же ключ 'self', что и у чипа «Я», см. buildFilter). Остальных людей по-прежнему
   * выбирают чипом внутри раздела — подсказка об этом гасится по клику (personHintDismissed). */
  readonly person = input<string | undefined>(undefined);
  personHintDismissed = false;

  private readonly overlays = inject(OverlayStackService);
  readonly state = inject(FamilyStateService);
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  readonly ai = inject(AiStatusService);
  private readonly confirm = inject(ConfirmService);
  private readonly toast = inject(ToastService);
  private readonly pageAction = inject(PageActionService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly breakpoints = inject(BreakpointService);

  /** Доступен в шаблоне для сравнения с this.kind(). */
  readonly Kind = MedicalRecordKind;
  readonly ExtractionJobStatus = ExtractionJobStatus;
  readonly ExtractionStatus = ExtractionStatus;
  readonly IndicatorFlag = IndicatorFlag;
  readonly pluralizeRu = pluralizeRu;

  /** Тултип чипа «уточняем норму…» (§5 + план "живой поток мыслей") — живая "мысль" модели, если
   * задача реально держит гейт LM Studio, иначе — позиция в общей очереди к LLM. */
  readonly shortenDisplayName = shortenDisplayName;
  readonly shortenDoctorName = shortenDoctorName;
  readonly formatDayMonth = formatDayMonth;
  readonly formatDayMonthYear = formatDayMonthYear;
  readonly formatYear = formatYear;

  /** Уникален на инстанс — «Анализы» и «Врачи» держат каждый свой экземпляр панели. */
  readonly doctorsDatalistId = `medical-record-doctors-datalist-${nextInstanceId++}`;

  items: MedicalRecord[] = [];
  loading = true;
  error: string | null = null;
  /** Информационное сообщение (не ошибка) — «уже в очереди»/«сервер недоступен» на попытке
   * «Распознать»: отдельно от error, чтобы не выглядеть как сбой (см. handleRecognize). */
  info: string | null = null;

  // --- Редизайн v2.2 — действия открытой записи видимыми кнопками (было — за «…»), Файлы и
  // Резюме сворачиваются по умолчанию и разворачиваются этими же кнопками. Одна запись в
  // singleMode — простых булевых достаточно, сбрасываются при смене id записи (эффект в
  // конструкторе, ветка recordId).
  filesOpen = false;
  summaryOpen = false;

  // --- Пагинация → бесконечная прокрутка (редизайн v2, PR3b) — группировка по человеку
  // несовместима с нумерованными страницами (у одного человека может быть занята вся страница,
  // см. риск Р2 плана редизайна). pageSize 50 (было 15); при активном текстовом поиске/фильтре
  // по врачу сервер и так уходит на in-memory путь (материализует весь видимый срез) — тогда
  // сразу просим 100 и не заводим сентинел (см. usingTextFilter/hasMore ниже).
  page = 1;
  readonly pageSize = 50;
  totalCount = 0;
  loadingMore = false;

  // --- Фильтры (UX-редизайн) — серверные, любое изменение сбрасывает страницу на 1. ---
  // Редизайн v3 — filtersOpen теперь управляет попапом/шторкой у кнопки «Фильтры» (было —
  // раскрытием app-expandable над списком), см. #filtersAnchor/onDocumentClick ниже.
  filtersOpen = false;
  @ViewChild('filtersAnchor') private filtersAnchorRef?: ElementRef<HTMLElement>;
  filters = { from: '', to: '', patientKey: 'all', doctor: '' };
  searchQuery = '';
  private searchDebounceHandle: ReturnType<typeof setTimeout> | null = null;

  /** Редизайн v2.2 — на мобиле показатель открывается отдельным экраном с настоящим URL
   * (?indicator=), не нижним листом; тот же приём, что kb-analyte-tab.component.ts уже
   * применяет для ?id=. На wide экранах эта подписка ничего не делает — панель справки там
   * остаётся чисто in-memory состоянием, как и раньше. */
  private indicatorParamSub?: Subscription;

  /** Автоподсказка «Врач» (v2) — доктора, которых пользователь уже вводил в своих записях;
   * грузится один раз, независимо от вида записи (общий пул для «Анализов» и «Врачей»). Форма
   * создания (редизайн v3, PR7) переехала на отдельный роут (record-add.component.ts) и грузит
   * её независимо — тот же дешёвый идемпотентный GET, дублировать состояние ради одного запроса
   * не стоит. */
  doctorSuggestions: string[] = [];

  // Распознавание — результат живёт на уровне ЗАПИСИ (не вложения): повторное распознавание
  // любого вложения записи полностью заменяет предыдущие показатели/резюме этой записи (см.
  // MedicalDocumentExtractionProcessor). Индикаторы/резюме/заключение грузятся сразу в refresh()
  // для Ready-записей ТЕКУЩЕЙ СТРАНИЦЫ (не более pageSize, было — не более общего числа записей).
  extractionStatusByRecord: Record<string, ExtractionStatusResponse | null> = {};
  indicatorsByRecord: Record<string, IndicatorDto[]> = {};
  summaryByRecord: Record<string, RecordSummaryResponse | null> = {};
  /** Id записи, для которой сейчас идёт пересчёт резюме (см. regenerateSummary) — дизейблит
   * кнопку именно этой записи и переключает её подпись на "Пересчитываем…". */
  summaryRegeneratingRecordId: string | null = null;
  conclusionByRecord: Record<string, VisitConclusion | null> = {};
  /** Id записей, для которых сейчас идёт запрос «Распознать» — дизейблит кнопку именно этих
   * записей. Множество, а не одиночный id (было раньше) — единственная задача-на-запись
   * (v2 задача — на всю запись, не вложение), но одновременно распознаваться могут НЕСКОЛЬКО РАЗНЫХ
   * записей (у одного пользователя открыты и «Анализы», и «Врачи», или несколько записей подряд) —
   * одиночный id затирал бы отметку предыдущей записи, преждевременно разблокируя её кнопку, пока
   * та ещё реально распознаётся на бэкенде (баг, найденный на живом отчёте). */
  recognizingRecordIds = new Set<string>();
  /** Живой список шагов на карточку (UX-редизайн) — история, не только текущая стадия, см.
   * shared/pipeline-progress. */
  pipelineStepsByRecord: Record<string, PipelineStep[]> = {};
  private readonly pipelineClearHandles = new Map<string, ReturnType<typeof setTimeout>>();
  /** Опрос статуса распознавания по записям — см. extraction-poller.ts (сбой одного запроса не
   * останавливает опрос, медленный режим, пока задача ждёт ИИ). */
  private readonly extractionPoller = new ExtractionPoller<MedicalRecord>({
    fetchStatus: (record) => this.api.getExtractionStatus(record.id),
    onStatus: (record, status) => {
      const prev = this.extractionStatusByRecord[record.id] ?? null;
      this.extractionStatusByRecord = { ...this.extractionStatusByRecord, [record.id]: status };
      this.updatePipelineSteps(record.id, status, prev);
    },
    onFinished: async (record, status) => {
      this.setRecognizing(record.id, false);
      if (status.status === ExtractionJobStatus.Completed) {
        await this.loadExtractionResult(record);
        this.appendEnrichmentFollowupStep(record.id);
        await this.refresh({ silent: true });
        // Прячем виджет только после успеха. Сбой остаётся на экране, пока человек не уйдёт со
        // страницы: раньше сообщение об ошибке исчезало через 2,5 с, и его легко было не заметить.
        this.schedulePipelineClear(record.id);
      }
    },
    onGaveUp: (record, err) => {
      this.setRecognizing(record.id, false);
      this.error = err instanceof ApiError ? err.message : 'Не удалось получить статус распознавания.';
    },
  }, {
    intervalMs: EXTRACTION_POLL_INTERVAL_MS,
    waitingIntervalMs: WAITING_POLL_INTERVAL_MS,
    maxFailures: MAX_CONSECUTIVE_POLL_FAILURES,
  });
  /** §5 плана «живой конвейер» — отдельный от extractionPoller поллинг: тот следит за самой
   * экстракцией (короче, до "Готово"), этот — за обогащением ОТДЕЛЬНЫХ показателей после неё
   * (может идти и после того, как экстракция давно завершилась). См. syncEnrichmentPolling. */
  private readonly enrichmentPollHandles = new Map<string, ReturnType<typeof setInterval>>();
  /** Поллинг резюме, пока оно помечено pending (после ручной правки бэк пересчитывает его в фоне —
   * RecordSummaryRegenerationJob, дебаунс 15 с). Останавливается сам, когда pending уходит. */
  private readonly summaryPollHandles = new Map<string, ReturnType<typeof setInterval>>();
  private readonly summaryPollAttempts = new Map<string, number>();
  /** Записи, для которых при открытии «Резюме» уже пробовали составить его автоматически —
   * одна попытка на запись за сессию, чтобы недоступный LLM не дёргался на каждый клик. */
  private readonly summaryAutoTried = new Set<string>();

  // --- Правка/добавление показателя вручную (ошибка OCR, v2 + UX-редизайн) ---
  readonly RefSource = RefSource;
  editingIndicatorId: string | null = null;
  editIndicatorForm: UpdateIndicatorRequest = emptyIndicatorForm();
  /** Стабильная пустая ссылка для [indicators], пока показатели записи не загружены. */
  readonly noIndicators: readonly IndicatorDto[] = [];

  indicatorsOf(recordId: string): readonly IndicatorDto[] {
    return this.indicatorsByRecord[recordId] ?? this.noIndicators;
  }
  savingIndicator = false;
  /** Id записи, для которой сейчас открыта строка «+ Добавить показатель» (null — закрыта). */

  // L1: семьи, которым владелец глобально расшарил записи (общее для обоих видов — единый шаринг).
  shares: string[] = [];

  // Запись, для которой сейчас открыта шторка «Доступ» (null — шторка закрыта).
  accessRecord: MedicalRecord | null = null;

  /** Группа (по ключу человека), для которой сейчас открыт список-пикер «какую запись
   * настроить» — редизайн v2, «Изменить доступ» теперь на уровне человека, а сама мутация
   * доступа по-прежнему делается по одной записи (механику шаринга не меняем). Группа из одной
   * записи пропускает пикер и сразу открывает её собственную шторку — см. openGroupAccess(). */
  groupAccessPickerKey: string | null = null;

  // --- Правка даты/врача/описания записи (кнопка «Редактировать», UX-редизайн) ---
  editRecord: MedicalRecord | null = null;
  /** Подсказка модели для поля «Биоматериал» (из баннера «уточните»). */
  editSpecimenHint: string | null = null;

  // --- Справка по назначенному лекарству (заключение врача, UX-редизайн) — та же карточка и
  // тот же bottom-sheet, что во вкладке «Справочник» (kb-tab.component.ts). ---
  kbCardOpen = false;
  kbCardLoading = false;
  kbCardError: string | null = null;
  selectedKbCard: KbMedicationCard | null = null;

  // undefined — ещё ни разу не загружали.
  private loadedKind: MedicalRecordKind | undefined = undefined;
  /** Редизайн v3 — отдельно от loadedKind: одиночный режим может смениться на другую запись
   * того же вида (тот же роут, другой :id), не только при смене kind. */
  private loadedRecordId: string | null | undefined = undefined;

  constructor() {
    // Реагирует на смену вида/записи, пока панель смонтирована (сейчас оба вида монтируются на
    // разных страницах, но контракт Panel требует этого независимо — см.
    // medkits-panel.component.ts).
    effect(() => {
      const kind = this.kind();
      const recordId = this.recordId();
      if (kind === this.loadedKind && recordId === this.loadedRecordId) return;
      this.loadedKind = kind;
      this.loadedRecordId = recordId;

      if (recordId) {
        // Одиночный режим (PR6) — не список: без пагинации/фильтров/формы создания, кнопка
        // «Добавить» и общий поиск шапки этому экрану не нужны (см. ngOnInit — та же проверка).
        // Редизайн v2.2 — сама открытая запись рисует свою шапку (back-link + действия), общий
        // топбар каркаса целиком не нужен, см. PageActionService.immersive.
        this.pageAction.setImmersive(true);
        this.filesOpen = false;
        this.summaryOpen = false;
        void this.refresh();
        return;
      }

      this.pageAction.setImmersive(false);
      this.resetFilters();
      this.accessRecord = null;
      this.page = 1;
      void this.refresh();
      // Редизайн v3 — кнопка «Добавить» уводит на отдельный роут record-add.component.ts
      // (боковая панель на десктопе, полноэкранно на мобильном), не разворачивает форму инлайн
      // (было — createOpen, см. PR7 плана).
      this.pageAction.set({
        label: this.labels.addButtonLabel,
        icon: 'ph-bold ph-plus',
        handler: () => this.openCreate(),
      });
      // Батч-загрузка (несколько документов одного пациента, каждый — своя запись/свой прогон
      // пайплайна) — рядом с «+ Добавить», не заменяет его: тот сценарий (страницы одного бланка)
      // остаётся отдельным. См. class doc RecordBatchAddComponent.
      this.pageAction.setSecondaryAction({
        label: 'Загрузить несколько',
        icon: 'ph-bold ph-stack',
        handler: () => { void this.router.navigate([this.kindBasePath(), 'batch']); },
      });
      // Редизайн v2.1 — своё поле поиска отдаётся топбару целиком (было — рисовалось инлайн под
      // заголовком экрана, общий поиск шапки просто подавлялся), см. PageActionService.pageSearch.
      this.pageAction.setPageSearch({
        placeholder: this.labels.searchPlaceholder,
        value: () => this.searchQuery,
        onChange: (v) => this.onSearchQueryChange(v),
      });
    });
  }

  ngOnInit(): void {
    // ?person=me — до первого refresh() ниже, чтобы список сразу загрузился уже отфильтрованным
    // (не мигал «все записи» на долю секунды перед переключением на «я»).
    if (this.person() === 'me' && this.filters.patientKey === 'all') this.filters.patientKey = 'self';

    // Первичная загрузка — здесь, а не только в effect(): effect выполняется на следующем цикле
    // change detection и может не успеть отработать до первого рендера шаблона. Та же причина —
    // почему кнопка топбара тоже дублируется явно здесь (иначе на первом рендере топбар недолго
    // показывался бы без действия).
    if (this.kind() !== this.loadedKind || this.recordId() !== this.loadedRecordId) {
      void this.refresh();
    }
    // Одиночный режим (PR6) — не список, кнопка «Добавить»/общий поиск шапки этому экрану не нужны.
    if (!this.recordId()) {
      this.pageAction.set({
        label: this.labels.addButtonLabel,
        icon: 'ph-bold ph-plus',
        handler: () => this.openCreate(),
      });
      this.pageAction.setSecondaryAction({
        label: 'Загрузить несколько',
        icon: 'ph-bold ph-stack',
        handler: () => { void this.router.navigate([this.kindBasePath(), 'batch']); },
      });
      this.pageAction.setPageSearch({
        placeholder: this.labels.searchPlaceholder,
        value: () => this.searchQuery,
        onChange: (v) => this.onSearchQueryChange(v),
      });
    } else {
      this.pageAction.setImmersive(true);
    }
    if (this.doctorSuggestions.length === 0) {
      void this.api.getDoctorSuggestions().then((doctors) => (this.doctorSuggestions = doctors));
    }
    this.indicatorParamSub = this.route.queryParamMap.subscribe(() => this.syncIndicatorFromRoute());
  }

  /** Опрос статуса распознавания использует setInterval — без явной остановки таймеры
   * пережили бы размонтирование панели (переключение вкладки Health-хаба). */
  ngOnDestroy(): void {
    this.extractionPoller.stopAll();
    for (const handle of this.enrichmentPollHandles.values()) clearInterval(handle);
    this.enrichmentPollHandles.clear();
    for (const handle of this.summaryPollHandles.values()) clearInterval(handle);
    this.summaryPollHandles.clear();
    for (const handle of this.pipelineClearHandles.values()) clearTimeout(handle);
    this.pipelineClearHandles.clear();
    if (this.searchDebounceHandle) clearTimeout(this.searchDebounceHandle);
    this.indicatorParamSub?.unsubscribe();
    this.pageAction.clear();
  }

  get labels(): MedicalRecordKindLabels {
    return MEDICAL_RECORD_KIND_LABELS[this.kind()];
  }

  /** Короткое название для заголовка карточки «Дата · Название · Пациент» (UX-редизайн) —
   * item.title, если уже распознан/введён, иначе нейтральная подпись по виду записи. */
  shortName(item: MedicalRecord): string {
    return item.title ?? (item.kind === MedicalRecordKind.Analysis ? 'Анализ' : 'Приём врача');
  }

  /** Базовый роут вида записи — используется и «Добавить»-навигацией (PR7), и мобильной
   * навигацией на экран открытой записи (PR6), см. shared/util/medical-record-labels.ts. */
  kindBasePath(): string {
    return medicalRecordKindBasePath(this.kind());
  }

  /** Редизайн v2.1 — клик по любому месту строки списка открывает запись отдельной страницей,
   * на любой ширине экрана (было — только на мобильном; на десктопе запись раскрывалась инлайн
   * кнопкой «Открыть», до которой мышью не всегда удобно дотягиваться, см. жалобу «клик по самой
   * строке анализа на десктопе также должен раскрывать анализ»). В singleMode (сама страница
   * записи) — no-op, там навигация не нужна. */
  onRecordCardClick(item: MedicalRecord, singleMode: boolean): void {
    if (singleMode) return;
    void this.router.navigate([this.kindBasePath(), item.id]);
  }

  goToList(): void {
    void this.router.navigate([this.kindBasePath()]);
  }

  // --- Группировка по человеку + таймлайн (редизайн v2, PR3b) ---

  /** Тот же ключ, что уже использует фильтр «Пациент» (patientOptions) — группа-человек и
   * чип-фильтр относятся к одному и тому же понятию "человек", не два независимых. */
  personKey(item: MedicalRecord): string {
    if (item.familyDependentId) return `dep:${item.familyDependentId}`;
    return `user:${item.targetUserId ?? item.ownerUserId}`;
  }

  /** items уже отсортированы сервером по RecordDate DESC — группировка Map'ом сохраняет порядок
   * первого появления, поэтому группы идут в порядке даты самой свежей записи каждого человека,
   * а записи внутри группы остаются в исходном (тоже дата-убывающем) порядке сервера. */
  get groupedItems(): RecordGroup[] {
    const groups = new Map<string, RecordGroup>();
    for (const item of this.items) {
      const key = this.personKey(item);
      let group = groups.get(key);
      if (!group) {
        group = { key, personName: item.personName, records: [] };
        groups.set(key, group);
      }
      group.records.push(item);
    }
    return [...groups.values()];
  }

  /** Инициалы аватара — см. shared/util/person-name.ts (переиспользуется и в indicators-tab). */
  personAvatarParts(name: string): { firstName: string; lastName: string | null } {
    return personAvatarPartsFromName(name);
  }

  private dependentFamilyName(dependentId: string): string | null {
    for (const family of this.state.activeFamilies()) {
      if ((family.dependents ?? []).some((d) => d.id === dependentId)) return family.name;
    }
    return null;
  }

  /** Доступ на уровне человека, а не под каждой записью (см. ТЗ редизайна) — четыре случая:
   * подопечный (структурно видит вся семья подопечного), чужая запись (расшарено/назначено
   * мне — менять нечего), мои записи с одинаковым accessSummary (показываем его), мои записи с
   * разным accessSummary («Доступ настроен по-разному» — правится по одной через пикер). */
  groupAccessLabel(group: RecordGroup): string {
    const first = group.records[0];

    if (first.familyDependentId) {
      const familyName = this.dependentFamilyName(first.familyDependentId);
      return familyName ? `Видит вся семья «${familyName}»` : 'Видит семья подопечного';
    }

    const myUserId = this.auth.me()?.userId;
    if (first.ownerUserId !== myUserId) {
      return 'Вам открыл(а) доступ';
    }

    const summaries = new Set(group.records.map((r) => this.accessSummary(r)));
    return summaries.size === 1 ? [...summaries][0] : 'Доступ настроен по-разному';
  }

  /** Доступ подопечного структурный (видимость всей семье по модели, не по L1/L2-шарингу) —
   * менять через «Доступ» нечего, ссылка скрывается. Чужие записи — то же самое: только владелец
   * управляет своим шарингом (инвариант 2 брифа), не мы. */
  groupCanChangeAccess(group: RecordGroup): boolean {
    const first = group.records[0];
    return !first.familyDependentId && first.ownerUserId === this.auth.me()?.userId;
  }

  /** Группа из одной записи — сразу её шторка, без лишнего промежуточного пикера. Несколько
   * записей — пикер «какую запись настроить» (см. groupAccessPickerKey), сама мутация доступа
   * по-прежнему делается по одной записи существующими openAccessSheet/setFamilyAccess. */
  openGroupAccess(group: RecordGroup): void {
    if (group.records.length === 1) {
      this.openAccessSheet(group.records[0]);
    } else {
      this.groupAccessPickerKey = group.key;
    }
  }

  closeGroupAccessPicker(): void {
    this.groupAccessPickerKey = null;
  }

  pickRecordForAccess(record: MedicalRecord): void {
    this.groupAccessPickerKey = null;
    this.openAccessSheet(record);
  }

  /** Действия меню «…» карточки — заменяет 4 безымянные иконки (редизайн v2). «Файлы» отсюда
   * убраны (редизайн): секция «Файлы» теперь всегда видна прямо в раскрытой карточке
   * (app-attachment-list), отдельный пункт меню/модалка больше не нужны. */
  recordMenuActions(item: MedicalRecord): ActionMenuItem[] {
    const actions: ActionMenuItem[] = [];
    if (this.canDelete(item)) {
      actions.push({ label: 'Редактировать', icon: 'ph ph-pencil-simple', handler: () => this.openEditSheet(item) });
    }
    actions.push({ label: 'Доступ', icon: 'ph ph-share-network', handler: () => this.openAccessSheet(item) });
    if (this.canDelete(item)) {
      actions.push({ label: 'Удалить', icon: 'ph ph-trash', danger: true, handler: () => void this.handleDelete(item) });
    }
    return actions;
  }

  toggleFiles(): void {
    this.filesOpen = !this.filesOpen;
  }

  toggleSummary(): void {
    this.summaryOpen = !this.summaryOpen;
    const only = this.items[0];
    if (this.summaryOpen && only) void this.ensureSummary(only);
  }

  /** Резюме составляется автоматически (кнопки «Пересчитать» больше нет): при первом открытии
   * секции без готового текста просим бэк составить его сразу. Только владельцу — эндпоинт
   * пересчёта owner-only, а у расшаренной записи чужого человека резюме появится, когда его
   * составит владелец. Если пересчёт уже идёт в фоне (pending) — резюме в summaryByRecord уже
   * есть (пусть и без текста), и его дожидается поллинг. */
  private async ensureSummary(record: MedicalRecord): Promise<void> {
    if (!this.canDelete(record) || this.summaryByRecord[record.id] || this.summaryAutoTried.has(record.id)) return;
    if (this.summaryRegeneratingRecordId === record.id) return;
    this.summaryAutoTried.add(record.id);
    await this.regenerateSummary(record.id);
  }

  /** Есть ли что сворачивать/разворачивать кнопкой «Резюме» — тот же гейт, что раньше стоял
   * прямо над блоком резюме (единственное место, где он проверялся). */
  hasSummarySection(item: MedicalRecord): boolean {
    return item.kind === MedicalRecordKind.Analysis && this.indicatorsOf(item.id).length > 0;
  }

  /** Третья плитка статуса — «без нормы в бланке». abnormalIndicatorCount/normalIndicatorCount
   * уже приходят с сервера (см. чип списка) — без нормы просто остаток, отдельно не считаем. */
  unknownIndicatorCount(item: MedicalRecord): number {
    return Math.max(0, item.indicatorCount - item.abnormalIndicatorCount - item.normalIndicatorCount);
  }

  /** Редизайн v2.1 — «скан» переименовано в «файл»: вложение не обязательно скан (PDF, фото с
   * телефона), «скан» вводил в заблуждение. */
  attachmentCountLabel(item: MedicalRecord): string {
    return `${item.attachmentCount} ${pluralizeRu(item.attachmentCount, 'файл', 'файла', 'файлов')}`;
  }

  indicatorCountLabel(item: MedicalRecord): string {
    return `${item.indicatorCount} ${pluralizeRu(item.indicatorCount, 'показатель', 'показателя', 'показателей')}`;
  }

  /** «Я» + все подопечные и все другие активные участники из моих активных семей — общая
   * реализация с record-add.component.ts (форма создания), см. shared/util/patient-options.ts. */
  get patientOptions(): PatientOption[] {
    return buildPatientOptions(this.state.activeFamilies(), this.auth.me()?.userId);
  }

  /** Фильтр «Пациент» — те же опции + «Все» сверху (форма создания «Все» не предлагает,
   * там пациент обязателен). */
  get filterPatientOptions(): PatientOption[] {
    return [{ key: 'all', familyDependentId: null, targetUserId: null, label: 'Все' }, ...this.patientOptions];
  }

  // --- Фильтры/поиск/пагинация ---

  /** Сколько фильтров сейчас активно — счётчик на заголовке кнопки «Фильтры». */
  get activeFilterCount(): number {
    let count = 0;
    if (this.filters.from) count++;
    if (this.filters.to) count++;
    if (this.filters.patientKey !== 'all') count++;
    if (this.filters.doctor.trim()) count++;
    return count;
  }

  onFilterChange(): void {
    this.page = 1;
    void this.refresh();
  }

  /** Редизайн v2 — чипы-фильтр по человеку наверху экрана (было — только внутри "Фильтры",
   * дропдауном). Тот же filters.patientKey/onFilterChange, что и раньше — фильтр один, просто
   * теперь у него два входа (заметный чип + всё ещё доступный список внутри "Фильтры" не нужен,
   * убран как дублирующий). */
  selectPatientFilter(key: string): void {
    if (this.filters.patientKey === key) return;
    this.filters.patientKey = key;
    this.onFilterChange();
  }

  /** Подсказка «Из хаба раздел открывается на вас…» (макет «Screen - Health hub», мобильный экран
   * «Анализы») — закрывается по клику, дальше не появляется до перезахода на страницу. */
  dismissPersonHint(): void {
    this.personHintDismissed = true;
  }

  resetFilters(): void {
    this.filters = { from: '', to: '', patientKey: 'all', doctor: '' };
    this.searchQuery = '';
    this.page = 1;
    void this.refresh();
  }

  /** Закрытие попапа «Фильтры» по клику вне него — тот же приём, что shared/action-menu (мобильная
   * шторка закрывается сама через popstate/бэкдроп, здесь нужен только десктопный попап). */
  @HostListener('document:click', ['$event'])
  onDocumentClickForFilters(event: MouseEvent): void {
    if (!this.filtersOpen || !this.isWide) return;
    if (!this.filtersAnchorRef?.nativeElement.contains(event.target as Node)) {
      this.filtersOpen = false;
    }
  }

  @HostListener('document:keydown.escape', ['$event'])
  onEscapeForFilters(event: KeyboardEvent): void {
    if (event.defaultPrevented || this.overlays.hasOpen()) return;
    if (this.filtersOpen && this.isWide) this.filtersOpen = false;
  }

  onSearchQueryChange(value: string): void {
    this.searchQuery = value;
    if (this.searchDebounceHandle) clearTimeout(this.searchDebounceHandle);
    this.searchDebounceHandle = setTimeout(() => {
      this.page = 1;
      void this.refresh();
    }, SEARCH_DEBOUNCE_MS);
  }

  /** На in-memory пути (поиск/фильтр по врачу — Doctor/Title/Description зашифрованы, SQL по ним
   * невозможен, ADR-0002) сервер и так материализует весь видимый срез перед фильтрацией —
   * бесконечная прокрутка на этом пути только пересчитывала бы его на каждую подгрузку. Вместо
   * этого просим сразу до MaxPageSize=100 и не заводим сентинел (см. hasMore/loadMore). */
  get usingTextFilter(): boolean {
    return !!(this.searchQuery.trim() || this.filters.doctor.trim());
  }

  hasMore(): boolean {
    return !this.usingTextFilter && this.items.length < this.totalCount;
  }

  private buildFilter(): MedicalRecordFilter {
    const opt = this.filters.patientKey === 'all' || this.filters.patientKey === 'self'
      ? null
      : this.patientOptions.find((o) => o.key === this.filters.patientKey);
    return {
      kind: LIST_KIND_TOKEN[this.kind()],
      from: this.filters.from || undefined,
      to: this.filters.to || undefined,
      dependentId: opt?.familyDependentId ?? undefined,
      targetUserId: opt?.targetUserId ?? undefined,
      // subject: 'me', не self (TECH_DEBT.md #17) — self пропускает чужие записи, которые
      // владелец расшарил моей семье; MineOnly (subject=me) этой дыры не имеет.
      subject: this.filters.patientKey === 'self' ? 'me' : undefined,
      doctor: this.filters.doctor.trim() || undefined,
      q: this.searchQuery.trim() || undefined,
      page: this.page,
      pageSize: this.usingTextFilter ? 100 : this.pageSize,
    };
  }

  /** Полная перезагрузка с первой страницы — вызывается на смену фильтров/вида/после
   * создания-удаления записи. Подгрузку СЛЕДУЮЩИХ страниц при скролле делает loadMore(), которая
   * дозаписывает в items, а не заменяет их. */
  /** silent — фоновое обновление (завершилось распознавание, переключили доступ, правка в панели):
   * без спиннера на весь список — раньше список мигал после каждого такого события. */
  async refresh(options?: { silent?: boolean }): Promise<void> {
    const kind = this.kind();
    const recordId = this.recordId();
    this.loadedKind = kind;
    this.loadedRecordId = recordId;
    this.page = 1;
    if (!options?.silent) this.loading = true;
    try {
      if (recordId) {
        // Одиночный режим (PR6) — одна запись по id, без пагинации/фильтров/группировки.
        const [record, shares] = await Promise.all([
          this.api.getMedicalRecord(recordId),
          this.api.getMedicalRecordShares(),
        ]);
        this.items = [record];
        this.totalCount = 1;
        this.shares = shares;
      } else {
        const [page, shares] = await Promise.all([
          this.api.getMedicalRecords(this.buildFilter()),
          this.api.getMedicalRecordShares(),
        ]);
        this.items = page.items;
        this.totalCount = page.totalCount;
        this.shares = shares;
      }
      // Открытая шторка должна остаться синхронной с перезагруженным состоянием записи.
      if (this.accessRecord) {
        this.accessRecord = this.items.find((r) => r.id === this.accessRecord!.id) ?? null;
      }
      this.error = null;

      // Показатели/резюме/заключение — только для готовых записей ТЕКУЩЕЙ страницы, не для
      // всего списка сразу (UX-редизайн — было главным источником N+1 вместе со вложениями).
      await Promise.all(
        this.items
          .filter((item) => item.extractionStatus === ExtractionStatus.Ready)
          .map((item) => this.loadExtractionResult(item)),
      );
      this.resumeLivePolling(this.items);
      // Редизайн v2.2 — ?indicator= в URL может прийти раньше, чем показатели этой записи
      // загрузятся (первый заход по ссылке/обновление страницы) — на момент первого срабатывания
      // подписки в ngOnInit indicatorsByRecord ещё пуст, повторяем попытку здесь.
      if (recordId) this.syncIndicatorFromRoute();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить данные.';
    } finally {
      this.loading = false;
    }
  }

  /** Подгрузка следующей «страницы» при появлении сентинела в вьюпорте — дозаписывает в items,
   * сохраняя уже раскрытые/загруженные показатели существующих карточек нетронутыми. */
  async loadMore(): Promise<void> {
    if (this.loadingMore || !this.hasMore()) return;
    this.loadingMore = true;
    this.page++;
    try {
      const page = await this.api.getMedicalRecords(this.buildFilter());
      this.items = [...this.items, ...page.items];
      this.totalCount = page.totalCount;
      await Promise.all(
        page.items
          .filter((item) => item.extractionStatus === ExtractionStatus.Ready)
          .map((item) => this.loadExtractionResult(item)),
      );
      this.resumeLivePolling(page.items);
    } catch (err) {
      this.page--; // откат — иначе следующая попытка пропустит эту страницу
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить ещё записи.';
    } finally {
      this.loadingMore = false;
    }
  }

  /** Безусловное удаление доступно только владельцу (кто физически загрузил) — сервер
   * перепроверит независимо от того, кому запись сейчас видна. */
  canDelete(record: MedicalRecord): boolean {
    return record.ownerUserId === this.auth.me()?.userId;
  }

  async handleDelete(record: MedicalRecord): Promise<void> {
    const confirmed = await this.confirm.confirm({
      title: 'Удалить запись?',
      message: 'Запись и все её вложения будут удалены безвозвратно.',
      confirmText: 'Удалить',
      danger: true,
    });
    if (!confirmed) return;

    try {
      await this.api.deleteMedicalRecord(record.id);
      if (this.accessRecord?.id === record.id) this.accessRecord = null;
      if (this.recordId() === record.id) {
        // Одиночный режим (открытая запись) — перечитывать здесь нечего, запись только что
        // удалена: refresh() позвал бы getMedicalRecord(recordId) и получил бы 404, оставляя
        // пользователя на пустом/устаревшем экране записи без какой-либо навигации (баг).
        this.goToList();
        return;
      }
      await this.refresh({ silent: true });
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось удалить запись.';
    }
  }

  // Файлы записи — теперь shared/attachment-list.component.ts, встроенный прямо в раскрытую
  // карточку (см. шаблон); own state/fetch/upload там, не здесь (было — модалка «Файлы»/методы
  // openFilesModal/handleUpload на этой панели).

  // --- Распознавание (кнопка «Распознать» на записи, v2 — обрабатывает все ещё не
  // распознанные вложения последовательно за один прогон, не по клику на каждый файл) ---

  /** Видимость кнопки «Распознать» — по счётчику из DTO, БЕЗ загрузки списка вложений. */
  /** Форма добавления — отдельный роут (кнопка в шапке и в пустом состоянии ведут сюда же). */
  openCreate(): void {
    void this.router.navigate([this.kindBasePath(), 'new']);
  }

  hasUnrecognizedAttachments(record: MedicalRecord): boolean {
    return record.unrecognizedAttachmentCount > 0;
  }

  async handleRecognize(record: MedicalRecord): Promise<void> {
    // Повторное распознавание ЗАМЕНЯЕТ показатели записи целиком (см. комментарий у
    // extractionStatusByRecord) — раньше ручные исправления молча пропадали после добавления ещё
    // одной страницы и нажатия «Распознать».
    if ((this.indicatorsByRecord[record.id]?.length ?? 0) > 0) {
      const ok = await this.confirm.confirm({
        title: 'Распознать заново?',
        message: 'Показатели и резюме этой записи будут заменены результатом нового распознавания — '
          + 'вместе с вашими ручными исправлениями.',
        confirmText: 'Распознать заново',
        danger: true,
      });
      if (!ok) return;
    }
    this.setRecognizing(record.id, true);
    this.clearPipelineTimer(record.id);
    this.pipelineStepsByRecord = {
      ...this.pipelineStepsByRecord,
      [record.id]: [{ id: 'queued', label: 'В очереди', state: 'active' }],
    };
    try {
      const response = await this.api.requestExtraction(record.id);
      this.error = null;
      // already_queued — не ошибка и не новая постановка в очередь (см. ExtractionRequestResult на
      // бэкенде): показываем как info-плашку и НЕ запускаем поллинг заново — существующая задача уже
      // опрашивается предыдущим вызовом (или будет подхвачена следующим обновлением списка).
      // waiting_for_ai — задача СОЗДАНА и ждёт возвращения ИИ: поллинг запускается как обычно, шаг
      // «ждём ИИ» появится с первым же статусом (см. updatePipelineSteps).
      if (response?.code === 'already_queued') {
        this.info = response.message ?? null;
        this.setRecognizing(record.id, false);
        this.pipelineStepsByRecord = { ...this.pipelineStepsByRecord, [record.id]: [] };
        return;
      }
      this.info = null;
      this.extractionStatusByRecord = { ...this.extractionStatusByRecord, [record.id]: null };
      this.startPolling(record);
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось запустить распознавание.';
      this.setRecognizing(record.id, false);
    }
  }

  /** Резюмирует живой прогресс распознавания на (пере)монтировании панели — без этого пользователь,
   * ушедший со страницы (или обновивший её, F5) во время работы фонового LLM-конвейера, не видел бы
   * вообще никакого признака, что распознавание всё ещё идёт, до следующего ручного клика
   * «Распознать» (баг). Опирается на item.extractionStatus === Pending — это поле теперь честно
   * проставляется бэкендом (ExtractionRequestService/MedicalDocumentExtractionProcessor), а не
   * только None/Ready, как было раньше. extractionPoller.isPolling — «эта запись опрашивается
   * прямо сейчас», вызов идемпотентен при повторных refresh(). */
  private resumeLivePolling(items: MedicalRecord[]): void {
    for (const item of items) {
      if (item.extractionStatus === ExtractionStatus.Pending && !this.extractionPoller.isPolling(item.id)) {
        this.setRecognizing(item.id, true);
        this.startPolling(item);
      }
    }
  }

  /** Единая точка мутации recognizingRecordIds (Set) — новый Set-инстанс на каждое изменение
   * (не мутация на месте), тот же приём иммутабельных обновлений, что и у Record-полей панели
   * (extractionStatusByRecord и т.п.) — гарантирует, что Angular увидит изменение ссылки. */
  private setRecognizing(recordId: string, on: boolean): void {
    const next = new Set(this.recognizingRecordIds);
    if (on) next.add(recordId); else next.delete(recordId);
    this.recognizingRecordIds = next;
  }

  private startPolling(record: MedicalRecord): void {
    this.extractionPoller.start(record);
  }

  /** Живой список шагов — логика в extraction-pipeline.ts (nextPipelineSteps). */
  private updatePipelineSteps(recordId: string, status: ExtractionStatusResponse, prev: ExtractionStatusResponse | null): void {
    const steps = nextPipelineSteps(this.pipelineStepsByRecord[recordId] ?? [], status, prev);
    this.pipelineStepsByRecord = { ...this.pipelineStepsByRecord, [recordId]: steps };
  }

  /** §6 плана «живой конвейер» — после «Готово» (см. вызов из startPolling.tick, ПОСЛЕ
   * loadExtractionResult — indicatorsByRecord должен быть уже свежим) предупреждаем, что часть
   * работы продолжится в фоне, если среди только что сохранённых показателей есть промахнувшиеся
   * по справочнику. Не активная/пульсирующая строка (state: 'done') — не блокирует «Готово», сам
   * живой статус конкретного показателя — уже у его чипа (§5)/у глобального индикатора (§4), эта
   * строка — просто финальная сводка перед тем, как весь виджет исчезнет (schedulePipelineClear). */
  private appendEnrichmentFollowupStep(recordId: string): void {
    const pendingCount = (this.indicatorsByRecord[recordId] ?? []).filter((i) => i.enrichmentPending).length;
    const step = enrichmentFollowupStep(pendingCount);
    if (!step) return;
    this.pipelineStepsByRecord = {
      ...this.pipelineStepsByRecord,
      [recordId]: [...(this.pipelineStepsByRecord[recordId] ?? []), step],
    };
  }

  private schedulePipelineClear(recordId: string): void {
    this.clearPipelineTimer(recordId);
    const handle = setTimeout(() => {
      const { [recordId]: _removed, ...rest } = this.pipelineStepsByRecord;
      this.pipelineStepsByRecord = rest;
      this.pipelineClearHandles.delete(recordId);
    }, PIPELINE_CLEAR_DELAY_MS);
    this.pipelineClearHandles.set(recordId, handle);
  }

  private clearPipelineTimer(recordId: string): void {
    const handle = this.pipelineClearHandles.get(recordId);
    if (handle) {
      clearTimeout(handle);
      this.pipelineClearHandles.delete(recordId);
    }
  }

  private async loadExtractionResult(record: MedicalRecord): Promise<void> {
    try {
      if (record.kind === MedicalRecordKind.Analysis) {
        const [indicators, summary] = await Promise.all([
          this.api.getRecordIndicators(record.id),
          this.api.getRecordSummary(record.id).catch(() => null),
        ]);
        this.indicatorsByRecord = { ...this.indicatorsByRecord, [record.id]: indicators };
        this.summaryByRecord = { ...this.summaryByRecord, [record.id]: summary };
        if (summary?.pending) this.pollSummary(record.id);
        this.syncEnrichmentPolling(record.id);
      } else {
        const conclusion = await this.api.getRecordConclusion(record.id).catch(() => null);
        this.conclusionByRecord = { ...this.conclusionByRecord, [record.id]: conclusion };
      }
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить результат распознавания.';
    }
  }

  /** §5 плана «живой конвейер» — самоостанавливающийся поллинг показателей ЗАПИСИ (тот же
   * принцип, что у BackgroundJobsStateService.refresh на уровень выше): пока хотя бы один
   * показатель enrichmentPending, перечитываем показатели каждые ENRICHMENT_POLL_INTERVAL_MS,
   * чтобы чип «уточняем норму…» сам пропал, когда обогащение завершится, без обновления страницы
   * — как только пропадает последний pending, останавливаем себя же. Вызывается после каждого
   * обновления indicatorsByRecord[recordId] (idempotent — новый вызов не плодит второй интервал).*/
  private syncEnrichmentPolling(recordId: string): void {
    const hasPending = (this.indicatorsByRecord[recordId] ?? []).some((i) => i.enrichmentPending);

    if (!hasPending) {
      const handle = this.enrichmentPollHandles.get(recordId);
      if (handle) {
        clearInterval(handle);
        this.enrichmentPollHandles.delete(recordId);
      }
      return;
    }

    if (this.enrichmentPollHandles.has(recordId)) return; // уже опрашивается

    const tick = async () => {
      try {
        const indicators = await this.api.getRecordIndicators(recordId);
        this.indicatorsByRecord = { ...this.indicatorsByRecord, [recordId]: indicators };
      } catch {
        // Транзиентный сбой — молча пробуем на следующем тике (тот же принцип, что и у основного
        // поллинга экстракции выше): фоновое обогащение продолжается на бэкенде независимо от
        // того, долетел ли этот один запрос.
        return;
      }
      this.syncEnrichmentPolling(recordId); // останавливает себя же, когда pending не осталось
    };

    this.enrichmentPollHandles.set(recordId, setInterval(() => void tick(), ENRICHMENT_POLL_INTERVAL_MS));
  }

  /** Синхронно составляет резюме по текущим показателям записи (POST .../summary/regenerate) —
   * нужно только когда готового резюме ещё нет (см. ensureSummary). После ручных правок бэк
   * пересчитывает резюме сам, в фоне — см. pollSummary. */
  private async regenerateSummary(recordId: string): Promise<void> {
    this.summaryRegeneratingRecordId = recordId;
    this.error = null;
    try {
      const summary = await this.api.regenerateRecordSummary(recordId);
      this.summaryByRecord = { ...this.summaryByRecord, [recordId]: summary };
      // 202 + pending: пересчёт поставлен в фон (при недоступном ИИ — дождётся его), ждём поллингом.
      if (summary.pending) this.pollSummary(recordId);
      else this.stopSummaryPoll(recordId);
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось составить резюме.';
    } finally {
      this.summaryRegeneratingRecordId = null;
    }
  }

  /** После ручной правки показателя/источника: перечитывает запись (счётчики плиток «вне нормы/в
   * норме» иначе остаются старыми) и резюме, которое бэк уже пометил устаревшим и пересчитывает в
   * фоне. Без спиннера на всю карточку — в отличие от refresh(). */
  private async afterRecordDataChanged(recordId: string): Promise<void> {
    try {
      const record = await this.api.getMedicalRecord(recordId);
      this.items = this.items.map((i) => (i.id === recordId ? record : i));
    } catch {
      // Счётчики обновятся при следующей загрузке — правка сама уже сохранена.
    }
    await this.loadSummary(recordId);
  }

  private async loadSummary(recordId: string): Promise<void> {
    const summary = await this.api.getRecordSummary(recordId).catch(() => null);
    this.summaryByRecord = { ...this.summaryByRecord, [recordId]: summary };
    if (summary?.pending) this.pollSummary(recordId);
    else this.stopSummaryPoll(recordId);
  }

  /** Опрашивает резюме, пока оно pending; само останавливается, когда пересчёт завершился (с
   * текстом или без него — при сбое LLM резюме сбрасывается). Потолок попыток — чтобы недоступный
   * бэк не опрашивался вечно. */
  private pollSummary(recordId: string): void {
    if (this.summaryPollHandles.has(recordId)) return;
    this.summaryPollAttempts.set(recordId, 0);
    this.summaryPollHandles.set(recordId, setInterval(() => {
      // Пока ИИ недоступен, пересчёт может ждать часами — потолок попыток считаем только когда он жив.
      const attempts = (this.summaryPollAttempts.get(recordId) ?? 0) + (this.ai.unavailable() ? 0 : 1);
      this.summaryPollAttempts.set(recordId, attempts);
      if (attempts > SUMMARY_POLL_MAX_ATTEMPTS) {
        this.stopSummaryPoll(recordId);
        return;
      }
      void this.loadSummary(recordId);
    }, SUMMARY_POLL_INTERVAL_MS));
  }

  private stopSummaryPoll(recordId: string): void {
    const handle = this.summaryPollHandles.get(recordId);
    if (handle) clearInterval(handle);
    this.summaryPollHandles.delete(recordId);
    this.summaryPollAttempts.delete(recordId);
  }

  // Отображение показателей — IndicatorTableComponent; панели нужны только эти два хелпера
  // (мета-строка записи и заголовок панели справки).
  readonly indicatorLabel = indicatorLabel;
  readonly specimenLabelFor = specimenLabelFor;

  // Раскрытие строки показателя (полное имя из бланка) — редизайн v2 заменил его на клик →
  // openIndicatorInfo(), полная информация теперь в панели справки, а не в самой строке.

  // --- Правка показателя вручную (ошибка OCR, v2) ---

  startEditIndicator(indicator: IndicatorDto): void {
    this.editingIndicatorId = indicator.id;
    this.editIndicatorForm = {
      displayName: indicator.displayName,
      valueRaw: indicator.valueRaw,
      unit: indicator.unit,
      refLowText: indicator.refLowText,
      refHighText: indicator.refHighText,
      refText: indicator.refText,
    };
  }

  cancelEditIndicator(): void {
    this.editingIndicatorId = null;
    this.editIndicatorForm = emptyIndicatorForm();
  }

  async saveEditIndicator(recordId: string): Promise<void> {
    if (!this.editingIndicatorId) return;
    if (!this.editIndicatorForm.displayName.trim()) {
      this.toast.error('Укажите название показателя.');
      return;
    }
    const savedId = this.editingIndicatorId;
    this.savingIndicator = true;
    try {
      await this.api.updateIndicator(savedId, sanitizeIndicatorForm(this.editIndicatorForm));
      const indicators = await this.api.getRecordIndicators(recordId);
      this.indicatorsByRecord = { ...this.indicatorsByRecord, [recordId]: indicators };
      this.syncEnrichmentPolling(recordId);
      this.cancelEditIndicator();
      this.error = null;
      // Редизайн v2.2 — редактирование теперь открывается прямо из панели справки (не из
      // таблицы): если правили именно тот показатель, чья статья сейчас открыта, панель должна
      // сразу показать новое значение/статус/шкалу, а не то, что было до правки.
      const updated = indicators.find((i) => i.id === savedId);
      if (updated && this.infoIndicatorId === savedId) void this.openIndicatorInfo(updated, false);
      void this.afterRecordDataChanged(recordId);
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось сохранить правку — возможно, такой показатель уже есть в записи.');
    } finally {
      this.savingIndicator = false;
    }
  }

  async deleteIndicatorRow(recordId: string, indicator: IndicatorDto): Promise<void> {
    const confirmed = await this.confirm.confirm({
      title: 'Удалить показатель?',
      message: `«${indicator.displayName}» будет удалён из записи безвозвратно.`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!confirmed) return;

    try {
      await this.api.deleteIndicator(indicator.id);
      const indicators = await this.api.getRecordIndicators(recordId);
      this.indicatorsByRecord = { ...this.indicatorsByRecord, [recordId]: indicators };
      this.syncEnrichmentPolling(recordId);
      if (this.infoIndicatorId === indicator.id) this.closeIndicatorInfo();
      void this.afterRecordDataChanged(recordId);
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось удалить показатель.');
    }
  }

  /** Показатель добавлен в IndicatorTableComponent — перечитываем показатели и саму запись
   * (счётчики «вне нормы / в норме»). */
  async onIndicatorCreated(recordId: string): Promise<void> {
    try {
      const indicators = await this.api.getRecordIndicators(recordId);
      this.indicatorsByRecord = { ...this.indicatorsByRecord, [recordId]: indicators };
      this.syncEnrichmentPolling(recordId);
      await this.refresh({ silent: true });
      this.error = null;
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Показатель добавлен, но список не обновился — обновите страницу.');
    }
  }

  // Открытие/скачивание вложения теперь целиком внутри app-file-viewer/attachment-list.

  // --- Доступ (bottom-sheet «Доступ») ---

  openAccessSheet(record: MedicalRecord): void {
    this.accessRecord = record;
  }

  closeAccessSheet(): void {
    this.accessRecord = null;
  }

  // --- Правка записи (bottom-sheet «Редактировать») ---

  /** Шторка «Редактировать запись» — см. record-edit-sheet.component.ts. hint предзаполняет
   * «Биоматериал» подсказкой модели, когда форма открыта из баннера «уточните». */
  openEditSheet(record: MedicalRecord, specimenHint?: string): void {
    this.editRecord = record;
    this.editSpecimenHint = specimenHint ?? null;
  }

  closeEditSheet(): void {
    this.editRecord = null;
  }

  async onRecordEdited(info: string | null): Promise<void> {
    if (info) this.info = info;
    this.closeEditSheet();
    await this.refresh({ silent: true });
    this.error = null;
  }

  // --- Справка по назначенному лекарству ---

  async openKbCard(kbMedicationId: string): Promise<void> {
    this.kbCardOpen = true;
    this.kbCardLoading = true;
    this.kbCardError = null;
    this.selectedKbCard = null;
    try {
      this.selectedKbCard = await this.api.getKbMedication(kbMedicationId);
    } catch (err) {
      this.kbCardError = err instanceof ApiError ? err.message : 'Не удалось загрузить карточку препарата.';
    } finally {
      this.kbCardLoading = false;
    }
  }

  closeKbCard(): void {
    this.kbCardOpen = false;
  }

  get isWide(): boolean {
    return this.breakpoints.tier() === 'wide';
  }

  // --- Справка по показателю (редизайн v2, PR4) — два пути открытия делят одно состояние:
  // клик по строке показателя записи (reading+history заданы, персонализировано под пациента
  // записи) и клик по чипу "что смотрят вместе" внутри уже открытой статьи (только карточка,
  // тот же путь, что каталог /health/kb/indicators).

  infoOpen = false;
  infoLoading = false;
  infoError: string | null = null;
  infoCard: KbAnalyteCard | null = null;
  infoDisplayName = '';
  infoReading: IndicatorInfoReading | null = null;
  infoHistory: IndicatorHistoryPoint[] | null = null;
  /** Редизайн v2.2 — возраст/пол пациента на дату записи, GET /api/indicators/{id}/article уже
   * отдаёт (response.patient), раньше просто игнорировался. */
  infoPatient: PatientContextDto | null = null;
  /** Id показателя, чья статья сейчас открыта reading-веткой — null, когда панель открыта чипом
   * "что смотрят вместе" (там нет конкретного показателя записи). Не путать с infoCard.id (это
   * id статьи справочника, другое значение). Не private — редизайн v2.2, шаблону нужен для
   * editing="editingIndicatorId === infoIndicatorId". */
  infoIndicatorId: string | null = null;
  /** Редизайн v2.2 — сам показатель (не только id), чтобы Редактировать/Удалить в панели справки
   * могли вызвать startEditIndicator/deleteIndicatorRow, которые принимают IndicatorDto целиком. */
  infoIndicator: IndicatorDto | null = null;

  /** navigate=false — вызов из самой подписки на маршрут (syncIndicatorFromRoute) или
   * переоткрытие после правки (saveEditIndicator): URL уже соответствует, повторная навигация
   * лишняя. По умолчанию true — обычный клик по строке/карточке показателя. */
  async openIndicatorInfo(indicator: IndicatorDto, navigate = true): Promise<void> {
    this.infoOpen = true;
    this.infoLoading = true;
    this.infoError = null;
    this.infoCard = null;
    this.infoHistory = null;
    this.infoPatient = null;
    this.infoIndicatorId = indicator.id;
    this.infoIndicator = indicator;
    this.infoDisplayName = this.indicatorLabel(indicator);
    // Редизайн v2.2 — на мобиле показатель открывается своим URL (?indicator=), не просто
    // in-memory состоянием: apparatus «назад» должен закрыть именно его, не всю запись (тот же
    // приём, что kb-analyte-tab уже применяет для ?id=). На wide экранах URL не трогаем — там
    // панель справки остаётся чисто in-memory, как и раньше.
    if (navigate && !this.isWide) {
      void this.router.navigate([], {
        relativeTo: this.route, queryParams: { indicator: indicator.id }, queryParamsHandling: 'merge',
      });
    }
    this.infoReading = {
      valueRaw: indicator.valueRaw,
      valueNumeric: indicator.valueNumericText !== null ? parseFloat(indicator.valueNumericText) : null,
      unit: indicator.unit,
      flag: indicator.flag,
      matchedRefRangeIndex: null,
    };
    try {
      const response = await this.api.getIndicatorArticle(indicator.id);
      this.infoCard = response.article;
      this.infoPatient = response.patient;
      this.infoReading = { ...this.infoReading, matchedRefRangeIndex: response.matchedRefRangeIndex };
      if (response.historyAvailable) {
        this.infoHistory = await this.api.getRecordIndicatorHistory(indicator.medicalRecordId, indicator.id);
      }
    } catch (err) {
      this.infoError = err instanceof ApiError ? err.message : 'Не удалось загрузить справку по показателю.';
    } finally {
      this.infoLoading = false;
    }
  }

  /** Чип "что смотрят вместе" внутри уже открытой статьи — переоткрываем панель БЕЗ
   * персонального контекста (это другой показатель, не тот, что открывал панель изначально). */
  async openRelatedAnalyte(kbAnalyteId: string): Promise<void> {
    this.infoOpen = true;
    this.infoLoading = true;
    this.infoError = null;
    this.infoCard = null;
    this.infoReading = null;
    this.infoHistory = null;
    this.infoPatient = null;
    this.infoDisplayName = '';
    this.infoIndicatorId = null;
    this.infoIndicator = null;
    try {
      this.infoCard = await this.api.getKbAnalyte(kbAnalyteId);
    } catch (err) {
      this.infoError = err instanceof ApiError ? err.message : 'Не удалось загрузить статью справочника.';
    } finally {
      this.infoLoading = false;
    }
  }

  /** navigate=false — вызов из syncIndicatorFromRoute (URL уже без ?indicator=) или там, где
   * следом всё равно уходим на другой URL (openIndicatorInCatalog) — см. openIndicatorInfo. */
  closeIndicatorInfo(navigate = true): void {
    this.infoOpen = false;
    this.infoIndicatorId = null;
    this.infoIndicator = null;
    this.cancelEditIndicator();
    if (navigate && !this.isWide) {
      void this.router.navigate([], {
        relativeTo: this.route, queryParams: { indicator: null }, queryParamsHandling: 'merge',
      });
    }
  }

  /** Редизайн v2.2 — синхронизирует infoOpen/infoIndicator* с ?indicator= в URL (мобильный
   * полноэкранный показатель). Вызывается из подписки на queryParamMap (ngOnInit) и из refresh()
   * — на первом срабатывании подписки indicatorsByRecord может быть ещё не загружен. */
  private syncIndicatorFromRoute(): void {
    if (this.isWide) return;
    const recordId = this.recordId();
    if (!recordId) return;
    const id = this.route.snapshot.queryParamMap.get('indicator');
    if (id) {
      if (this.infoIndicatorId === id) return;
      const found = (this.indicatorsByRecord[recordId] ?? []).find((i) => i.id === id);
      if (found) void this.openIndicatorInfo(found, false);
    } else if (this.infoOpen) {
      this.closeIndicatorInfo(false);
    }
  }

  /** Футер "Открыть в справочнике" — уходит на мини-хаб /health/kb/indicators с ?id=, тот же
   * экран сам откроет статью (см. KbAnalyteTabComponent.ngOnInit). */
  openIndicatorInCatalog(): void {
    if (!this.infoCard) return;
    const id = this.infoCard.id;
    this.closeIndicatorInfo(false); // уходим на другой роут ниже — чистить ?indicator= здесь незачем
    void this.router.navigate(['/health/kb/indicators'], { queryParams: { id } });
  }

  /** Сводка доступа для карточки и пикера группы — см. record-access.ts. */
  accessSummary(record: MedicalRecord): string {
    return accessSummary(record, this.shares);
  }

}
