// Типы зеркалят DTO бэкенда (System.Text.Json, минимальные API): свойства camelCase,
// enum'ы без JsonStringEnumConverter сериализуются как целые числа — см. FamilyHub.Domain.Enums.*.

export const FamilyRole = {Member: 0, Admin: 1} as const;

/** Зеркалит FamilyHub.Domain.Enums.Gender. */
export const Gender = {Male: 0, Female: 1} as const;
export type Gender = typeof Gender[keyof typeof Gender];

/** Зеркалит FamilyService.MaxFamiliesPerUser — макс. семей, которые может СОЗДАТЬ один
 * пользователь (см. аудит module-review-2026-08-02/02, находка 4). */
export const MAX_FAMILIES_PER_USER = 25;
export const MemberStatus = {PendingApproval: 0, Active: 1} as const;
export const RemoveMemberResult = {
    Removed: 0,
    Forbidden: 1,
    NotFound: 2,
    LastAdmin: 3
} as const;
export type RemoveMemberResult = typeof RemoveMemberResult[keyof typeof RemoveMemberResult];

export const NotificationType = {
    MedicationExpiringSoon: 0,
    MedicationExpired: 1,
    BirthdayUpcoming: 2,
    MemberLeft: 3,
    MemberApproved: 4,
    MedicalRecordShared: 5,
    MedicationEnriched: 6,
    MedicalDocumentExtracted: 7,
} as const;

export interface FamilySummary {
    id: string;
    name: string;
    myRole: number; // FamilyRole admin or member
    myStatus: number; // MemberStatus // active or pending to be active
    currentMembers: CurrentMember[] | null;
    dependents: FamilyDependent[] | null;
}

export interface PendingMember {
    userId: string;
    // ФИО тремя полями, не готовой строкой — форматирование под ширину экрана делает
    // shared/util/person-name.ts (person-name.component.ts).
    lastName: string | null;
    firstName: string | null;
    middleName: string | null;
    username: string | null;
    role: number; // FamilyRole
    joinedAt: string;
}


export interface CurrentMember {
    id: string;
    lastName: string | null;
    firstName: string | null;
    middleName: string | null;
    username: string | null;
    role: number; // FamilyRole
    joinedAt: string;
}


export interface InviteCreated {
    id: string;
    code: string;
    maxUses: number;
    expiresAt: string | null;
    /** Основная ссылка — ведёт на сайт (/join/:code), работает без Telegram. */
    webLink: string;
    /** Отдельная кнопка-самолётик — null, если бот не сконфигурирован (Telegram:BotUsername). */
    telegramLink: string | null;
}

/** Анонимный превью инвайта для лендинга /join/:code (см. InviteEndpoints.GetPreview) — без
 * персональных данных участников семьи, только то, что нужно гостю до входа/регистрации. */
export interface InvitePreview {
    familyName: string;
    inviterName: string | null;
}

/** Подопечный без своего User — ребёнок, питомец или пожилой родственник (семейный ресурс,
 * см. FamilyHub.Api.Features.Dependents). Не заводим фейковый User с синтетическим email.
 * firstName — имя человека или кличка питомца; lastName/middleName — только для людей (сервис
 * зануляет их при isPet === true). gender обязателен для всех, включая питомцев — используется
 * в напоминаниях о ДР (ReminderScanJob). */
export interface FamilyDependent {
    id: string;
    familyId: string;
    firstName: string;
    lastName: string | null;
    middleName: string | null;
    gender: number; // Gender
    birthDate: string | null;
    isPet: boolean;
    petSpecies: string | null;
    createdByUserId: string;
    createdAt: string;
}

export interface FamilyDependentInput {
    firstName: string;
    lastName: string | null;
    middleName: string | null;
    gender: number; // Gender
    birthDate: string | null;
    isPet: boolean;
    petSpecies: string | null;
}

export interface Medkit {
    id: string;
    familyId: string;
    name: string;
    createdByUserId: string;
    createdAt: string;
    medicationCount: number;
}

export interface MedkitInput {
    name: string;
}

export interface Medication {
    id: string;
    medkitId: string;
    familyId: string;
    name: string;
    expiryDate: string | null; // DateOnly "yyyy-MM-dd"
    // Всё остальное про медикамент — единым JSON: instructions, quantity (известные ключи,
    // под них в форме отдельные привычные инпуты) + что найдёт оцифровка по фото (manufacturer,
    // type, dose, mainActingAgent, любые доп. находки).
    data: Record<string, string>;
    createdByUserId: string;
    createdAt: string;
}

export interface MedicationInput {
    name: string;
    expiryDate: string | null;
    data: Record<string, string>;
}

/** Ответ POST /api/medications/ocr — результат оцифровки медикамента по фото локальной LLM. */
export interface MedicationOcrResponse {
    success: boolean;
    name: string | null;
    expiryDate: string | null; // dd/MM/yyyy, как вернула модель — конвертируется на фронте
    data: Record<string, string> | null;
    error: string | null;
}

/** Источник записи (identity rework) — Manual редактируема, Member/Dependent — производные
 * из профиля User/FamilyDependent, только для чтения (см. BirthdayService.GetForFamilyAsync). */
export const BirthdaySource = {Manual: 0, Member: 1, Dependent: 2} as const;
export type BirthdaySource = typeof BirthdaySource[keyof typeof BirthdaySource];

export interface Birthday {
    id: string;
    familyId: string;
    personName: string;
    date: string; // DateOnly "yyyy-MM-dd"
    source: number; // BirthdaySource
}

export interface BirthdayInput {
    personName: string;
    date: string;
}

/** Анализ или посещение врача — единая таблица на бэкенде (MedicalRecord.Kind). */
export const MedicalRecordKind = {Analysis: 0, DoctorVisit: 1} as const;
export type MedicalRecordKind = typeof MedicalRecordKind[keyof typeof MedicalRecordKind];

/** Статус распознавания записи (MedicalRecord.ExtractionStatus) — задачи 5.2/5.3. */
export const ExtractionStatus = {None: 0, Pending: 1, Ready: 2, Failed: 3} as const;
export type ExtractionStatus = typeof ExtractionStatus[keyof typeof ExtractionStatus];

export interface MedicalRecord {
    id: string;
    ownerUserId: string;
    kind: MedicalRecordKind;
    /** Резолвится сервером из familyDependentId/targetUserId/владельца (v2) — не хранится,
     * не редактируется напрямую (см. MedicalRecordService.ResolvePersonNamesAsync). */
    personName: string;
    recordDate: string;
    doctor: string | null;
    /** Короткое название ("Общий анализ крови") — из распознавания или введено вручную. */
    title: string | null;
    description: string | null;
    extractionStatus: ExtractionStatus;
    createdAt: string;
    /** L2: семьи, от которых точечно скрыта именно эта запись (отдаётся только владельцу). */
    hiddenFamilyIds: string[];
    /** Подопечный семьи, для которого загружена запись — видна всей активной семье подопечного
     * автоматически, без L1-шаринга. Взаимоисключимо с targetUserId. */
    familyDependentId: string | null;
    /** Участник семьи, для которого другой участник загрузил запись — видна ему напрямую.
     * ownerUserId при этом остаётся за тем, кто физически загрузил (только он может удалить —
     * см. api.deleteMedicalRecord). Взаимоисключимо с familyDependentId. */
    targetUserId: string | null;
    /** Счётчики (UX-редизайн) — считаются сервером одним GroupBy на страницу, чтобы карточка
     * знала, показывать ли «Распознать»/«Файлы (N)» БЕЗ отдельного GET /attachments на запись. */
    attachmentCount: number;
    unrecognizedAttachmentCount: number;
    indicatorCount: number;
    /** Редизайн v2 — чипы «N вне нормы»/«N в норме» на карточке списка (components/medical-records-panel).
     * «Без нормы» на фронте = indicatorCount − abnormalIndicatorCount − normalIndicatorCount. */
    abnormalIndicatorCount: number;
    normalIndicatorCount: number;
}

/** Постраничный ответ (UX-редизайн) — используется и для списка мед-записей, и для поиска. */
export interface PagedResult<T> {
    items: T[];
    page: number;
    pageSize: number;
    totalCount: number;
    totalPages: number;
}

/** Серверные фильтры списка мед-записей (UX-редизайн, GET /api/medical-records) — все опциональны. */
export interface MedicalRecordFilter {
    kind?: 'analysis' | 'visit';
    from?: string; // DateOnly "yyyy-MM-dd"
    to?: string;
    dependentId?: string;
    targetUserId?: string;
    self?: boolean;
    doctor?: string;
    q?: string;
    page?: number;
    pageSize?: number;
}

// personName убран (v2) — идентичность пациента выражается целиком через
// familyDependentId/targetUserId/владельца, отдельного текстового поля больше нет.
/** Правка существующей записи (UX-редизайн, кнопка «Редактировать») — только дата/врач/описание,
 * PUT /api/medical-records/{id}. Пациент и вид записи не редактируются. */
export interface UpdateMedicalRecordRequest {
    recordDate: string;
    doctor: string | null;
    description: string | null;
    /** Редизайн v3 (PR7) — та же семантика, что doctor/description: форма всегда шлёт текущее
     * значение, null/пустая строка явно очищает ранее выставленное распознаванием название. */
    title?: string | null;
}

export interface MedicalRecordInput {
    kind: MedicalRecordKind;
    recordDate: string;
    doctor: string | null;
    description: string | null;
    hideFromFamilyIds: string[] | null;
    familyDependentId: string | null;
    targetUserId: string | null;
}

export interface Attachment {
    id: string;
    fileName: string;
    contentType: string;
    sizeBytes: number;
    uploadedAt: string;
    /** Когда конвейер извлечения последний раз успешно распознал этот файл — null, если ещё
     * ни разу (v2: определяет, есть ли записи нечего распознавать кнопкой «Распознать»). */
    extractedAt: string | null;
}

/** Этап 3: пять источников с разным контролем доступа — см. FamilyHub.Modules.Medical.Search.SearchService.
 * Visit добавлен последним — перенумеровывать существующие значения нельзя (см. SearchDtos.cs). */
export const SearchResultType = { Medication: 0, Kb: 1, Record: 2, Birthday: 3, Visit: 4 } as const;
export type SearchResultType = typeof SearchResultType[keyof typeof SearchResultType];

/** Контекст лекарства в результате поиска — где оно лежит и до какого срока годно.
 * Заполнен только для SearchResultType.Medication (см. SearchService.SearchMedicationsAsync). */
export interface MedicationSearchContext {
    familyId: string;
    familyName: string;
    medkitId: string;
    medkitName: string;
    expiryDate: string | null; // DateOnly "yyyy-MM-dd"
}

/** Контекст дня рождения в результате поиска — в какой семье он записан и когда.
 * Заполнен только для SearchResultType.Birthday (см. BirthdaySearchSource на бэкенде). */
export interface BirthdaySearchContext {
    familyId: string;
    familyName: string;
    date: string; // DateOnly "yyyy-MM-dd"
}

export interface SearchResultItem {
    type: number; // SearchResultType
    id: string;
    title: string;
    snippet: string | null;
    score: number;
    birthday: BirthdaySearchContext | null;
    medication: MedicationSearchContext | null;
}

export interface SearchResponse {
    items: SearchResultItem[];
    page: number;
    pageSize: number;
    totalCount: number;
}

export interface VapidPublicKeyResponse {
    publicKey: string;
}

// Этап 4: общий обезличенный справочник препаратов, наполняемый AI-конвейером обогащения
// (OCR/ручной ввод → промах в справочнике → веб-поиск по доверенным РФ-источникам →
// суммаризация локальным Qwen → запись) — см. FamilyHub.Modules.Medical.Kb.

export interface KbListItem {
    id: string;
    displayName: string;
    purpose: string | null;
}

export interface KbListResponse {
    items: KbListItem[];
    /** Похоже, что есть ещё страница (столько же элементов, сколько запрошено) — точный total не считаем. */
    hasMore: boolean;
}

export interface KbMedicationCard {
    id: string;
    displayName: string;
    internationalName: string | null;
    tradeNames: string[];
    form: string | null;
    purpose: string | null;
    /** То же назначение простыми бытовыми словами, не медицинскими терминами (напр. "сбивает температуру"). */
    simplePurpose: string | null;
    /** Способ применения и дозы — как в официальной инструкции (общие данные, не для конкретного человека). */
    usage: string | null;
    storage: string | null;
    driving: string | null;
    specialNotes: string | null;
    /** Провайдер + домены-источники, напр. "brave: vidal.ru, rlsnet.ru" — для прослеживаемости знания. */
    source: string;
    updatedAt: string;
}

/** Статус обогащения конкретного медикамента пользователя (GET /api/medications/{id}/kb). */
export const MedicationKbStatus = { None: 0, Pending: 1, Running: 2, Failed: 3, Ready: 4 } as const;
export type MedicationKbStatus = typeof MedicationKbStatus[keyof typeof MedicationKbStatus];

export interface KbCandidate {
    kbId: string;
    displayName: string;
    score: number;
}

export interface MedicationKbResponse {
    status: number; // MedicationKbStatus
    /** Заполнена только при status === Ready. */
    card: KbMedicationCard | null;
    /** Неуверенная нечёткая привязка — предложить пользователю на подтверждение, не показывать как готовый ответ. */
    candidate: KbCandidate | null;
}

/** Итог ручного запроса «Уточнить в справочнике» (POST /api/medications/{id}/kb/refresh). */
export const EnrichmentRefreshStatus = { Requested: 0, NothingToRefresh: 1 } as const;
export type EnrichmentRefreshStatus = typeof EnrichmentRefreshStatus[keyof typeof EnrichmentRefreshStatus];

export interface EnrichmentRefreshOutcome {
    status: number; // EnrichmentRefreshStatus
    availableAt: string | null;
}

/** Предпочтения доставки по типу оповещения (вкладка «Настройки → Уведомления»). Записи
 * в /api/notifications создаются всегда — здесь только про push/Telegram-доставку. */
export interface NotificationPreference {
    type: number; // NotificationType
    pushEnabled: boolean;
    telegramEnabled: boolean;
}

export interface AppNotification {
    id: string;
    type: number; // NotificationType
    title: string;
    body: string;
    relatedEntityId: string;
    createdAt: string;
    isRead: boolean;
    readAt: string | null;
}

// Ветка medicalrecords (задачи 5.2/5.3): конвейер извлечения показателей анализов и заключений
// врача — см. FamilyHub.Modules.Medical.Extraction.

/** Итог сравнения показателя с референсным диапазоном (бланк приоритетнее справочника). */
export const IndicatorFlag = { Unknown: 0, Low: 1, Normal: 2, High: 3, Critical: 4 } as const;
export type IndicatorFlag = typeof IndicatorFlag[keyof typeof IndicatorFlag];

/** Откуда взят референс (v2, каскад приоритетов) — KbCalculated показывается на фронте
 * бэйджем «рассчитано ИИ». См. FamilyHub.Domain.Enums.RefSource. */
export const RefSource = { None: 0, Blank: 1, KbFixed: 2, KbCalculated: 3 } as const;
export type RefSource = typeof RefSource[keyof typeof RefSource];

/** Биоматериал показателя (v2) — часть ключа группировки вместе с analyteKey, иначе лейкоциты
 * крови и мочи слились бы на одном графике. См. FamilyHub.Domain.Enums.SpecimenType. */
export const SpecimenType = { Unknown: 0, Blood: 1, Urine: 2, Stool: 3, VaginalSwab: 4, Saliva: 5, Other: 6 } as const;
export type SpecimenType = typeof SpecimenType[keyof typeof SpecimenType];

/** Прогресс задачи распознавания внутри одного прогона — детальнее MedicalRecord.extractionStatus. */
export const ExtractionStage = { Queued: 0, Decoding: 1, Ocr: 2, Structuring: 3, Linking: 4, Summarizing: 5 } as const;
export type ExtractionStage = typeof ExtractionStage[keyof typeof ExtractionStage];

/** Статус самой задачи Hangfire (не путать с ExtractionStatus на MedicalRecord — тот проще). */
export const ExtractionJobStatus = { Pending: 0, Running: 1, Completed: 2, Failed: 3, Skipped: 4 } as const;
export type ExtractionJobStatus = typeof ExtractionJobStatus[keyof typeof ExtractionJobStatus];

export interface ExtractionStatusResponse {
    status: number; // ExtractionJobStatus
    stage: number; // ExtractionStage
    indicatorCount: number;
    error: string | null;
    /** v2: одна задача теперь обрабатывает ВСЕ ещё не распознанные вложения записи
     * последовательно — прогресс «файл N из totalFiles». */
    totalFiles: number;
    processedFiles: number;
    createdAt: string;
    completedAt: string | null;
}

export interface IndicatorDto {
    id: string;
    analyteKey: string;
    displayName: string;
    flag: number; // IndicatorFlag
    refSource: number; // RefSource
    specimen: number; // SpecimenType
    position: number;
    valueRaw: string;
    unit: string | null;
    refLowText: string | null;
    refHighText: string | null;
    refText: string | null;
    recordDate: string; // DateOnly "yyyy-MM-dd"
    medicalRecordId: string;
    /** Заполнено только при specimen === SpecimenType.Other — ссылка на UserSpecimen. */
    specimenCustomId: string | null;
    /** Редизайн v2 — invariant-culture double либо null (качественный результат без числа).
     * Гарантия та же, что у refLowText/refHighText: parseFloat без нормализации запятых. */
    valueNumericText: string | null;
    /** Редизайн v2 — ключ статьи справочника показателей; null, пока обогащение до него не дошло. */
    kbAnalyteId: string | null;
}

/** Ручная правка показателя (ошибка OCR), PUT /api/indicators/{id} — все поля целиком, не патч. */
export interface UpdateIndicatorRequest {
    displayName: string;
    valueRaw: string;
    unit: string | null;
    specimen: SpecimenType;
    refLowText: string | null;
    refHighText: string | null;
    refText: string | null;
    specimenCustomId?: string | null;
}

/** Ручное добавление показателя, POST /api/medical-records/{recordId}/indicators — та же форма,
 * что UpdateIndicatorRequest. */
export type CreateIndicatorRequest = UpdateIndicatorRequest;

/** Одна точка истории показателя (GET /api/indicators/{analyteKey}?specimen=&customId=) — для спарклайна. */
export interface IndicatorHistoryPoint {
    recordDate: string;
    valueRaw: string;
    /** Только если ValueRaw распарсился как число (invariant-culture) — иначе null (качественный результат). */
    valueNumericText: string | null;
    flag: number; // IndicatorFlag
    medicalRecordId: string;
}

/** Последнее значение по каждому (показателю, биоматериалу) среди СВОИХ записей (GET /api/indicators). */
export interface MyIndicatorSummary {
    analyteKey: string;
    displayName: string;
    specimen: number; // SpecimenType
    valueRaw: string;
    unit: string | null;
    flag: number; // IndicatorFlag
    lastRecordDate: string;
    specimenCustomId: string | null;
}

/** Биоматериал, которого нет в фиксированном SpecimenType — свой справочник пользователя
 * (UX-редизайн), провалидированный LLM один раз при создании (POST /api/specimens). */
export interface UserSpecimen {
    id: string;
    ownerUserId: string;
    normalizedName: string;
    displayName: string;
    createdAt: string;
}

/** Назначенный препарат (UX-редизайн) — kbMedicationId резолвится сервером живым поиском по
 * справочнику на каждое чтение (null, пока обогащение справочника ещё не завершилось). */
export interface PrescribedMedication {
    name: string;
    dosageInstructions: string | null;
    kbMedicationId: string | null;
}

/** Заключение врача (Kind=DoctorVisit), GET /api/medical-records/{id}/conclusion — MedicalRecord.ExtractedDataJson
 * + живой резолв ссылок на справочник медикаментов (UX-редизайн). */
export interface VisitConclusion {
    diagnosis: string | null;
    recommendations: string | null;
    anamnesis: string | null;
    proceduresPerformed: string | null;
    prescribedMedications: PrescribedMedication[];
}

export interface LabSummaryDeviation {
    name: string;
    meaning: string;
}

/** Форма MedicalRecord.SummaryJson (GET /api/medical-records/{id}/summary) — LLM-резюме анализа. */
export interface RecordSummaryResponse {
    plainSummary: string | null;
    deviations: LabSummaryDeviation[];
    questionsForDoctor: string[];
    disclaimer: string;
}

// Редизайн v2 — справочник показателей анализов (GET /api/kb/analytes[/{id}]), зеркало
// KbListItem/KbMedicationCard выше на другую таблицу (kb.global_lab_analytes_kb).

export interface KbAnalyteListItem {
    id: string;
    displayName: string;
    specimen: number; // SpecimenType — ключ справочника (показатель, биоматериал), см. shared/util/specimen.ts
    plainExplanation: string | null;
}

export interface KbAnalyteListResponse {
    items: KbAnalyteListItem[];
    hasMore: boolean;
}

/** normKind/population — см. FamilyHub.Domain.Enums.LabNormKind/LabPopulation, подписи в
 * shared/util/lab-norm.ts. sourceDomain — домен, выигравший при merge по приоритету источников
 * (null для строк, записанных до пересборки enrich-пайплайна). */
export interface KbRefRangeDto {
    ageFrom: number | null;
    ageTo: number | null;
    sex: number | null; // Gender | null — null означает "для обоих полов"
    low: number | null;
    high: number | null;
    unit: string | null;
    normKind: number; // LabNormKind
    population: number; // LabPopulation
    populationDetail: string | null;
    sourceDomain: string | null;
}

/** id=null — статьи по этому имени пока нет в справочнике (обогащение ещё не дошло) — чип
 * рендерится, но некликабелен. */
export interface KbRelatedAnalyte {
    id: string | null;
    displayName: string;
}

/** Aliases сознательно не отдаётся — тот же выбор, что у KbMedicationCard. */
export interface KbAnalyteCard {
    id: string;
    displayName: string;
    specimen: number; // SpecimenType
    loincCode: string | null;
    defaultUnit: string | null;
    plainExplanation: string | null;
    whyMeasured: string | null;
    highMeans: string | null;
    lowMeans: string | null;
    refRanges: KbRefRangeDto[];
    related: KbRelatedAnalyte[];
    source: string;
    updatedAt: string;
}

/** Возраст (на дату записи)/пол пациента — GET /api/indicators/{id}/article. */
export interface PatientContextDto {
    ageYears: number | null;
    sex: number | null; // Gender | null
}

/** Ответ GET /api/indicators/{id}/article — показатель + статья справочника + персональная
 * норма, одним запросом на клик по строке. article=null — показатель ещё не привязан к KB
 * (панель всё равно открывается, значение+шкала есть всегда). matchedRefRangeIndex — индекс в
 * article.refRanges, который нужно подсветить как "норма для этого человека". */
export interface IndicatorArticleResponse {
    indicator: IndicatorDto;
    patient: PatientContextDto;
    matchedRefRangeIndex: number | null;
    article: KbAnalyteCard | null;
    historyAvailable: boolean;
}

/** Лимиты загрузки вложений (GET /api/attachments/limits) — настраиваются в env, см. AttachmentUploadOptions. */
export interface AttachmentLimits {
    maxFileSizeBytes: number;
    maxFilesPerRecord: number;
}

// Редизайн v2 — агрегат Главной (GET /api/home/summary), см. FamilyHub.Api.Features.Home.

/** "expired" | "expiring" — считается на бэке (тот же порог, что у ReminderScanJob), фронт не
 * дублирует пороги. */
export type HomeMedicationSeverity = 'expired' | 'expiring';

export interface HomeMedicationAlert {
    medicationId: string;
    medkitId: string;
    medkitName: string;
    familyId: string;
    familyName: string;
    name: string;
    expiryDate: string; // DateOnly "yyyy-MM-dd"
    daysLeft: number; // отрицательное — просрочено
    severity: HomeMedicationSeverity;
}

/** Заявка на вступление в семью, где текущий пользователь — Admin. ФИО тремя полями — под
 * <app-person-name>, как PendingMember/CurrentMember. */
export interface HomeJoinRequest {
    familyId: string;
    familyName: string;
    userId: string;
    lastName: string | null;
    firstName: string | null;
    middleName: string | null;
    username: string | null;
    requestedAt: string;
}

export interface HomeBirthdayItem {
    familyId: string;
    familyName: string;
    personName: string;
    date: string; // DateOnly "yyyy-MM-dd"
    daysUntil: number;
    turningAge: number;
    source: number; // BirthdaySource
}

export interface HomeOkChips {
    medicationsInDate: number;
    medicationsTotal: number;
    analysesTotal: number;
    analysesAbnormal: number;
    pushEnabled: boolean;
}

export interface HomeSummaryResponse {
    greetingName: string | null;
    today: string; // DateOnly "yyyy-MM-dd"
    attentionTotal: number;
    primaryFamilyId: string | null;
    primaryFamilyName: string | null;
    medications: HomeMedicationAlert[];
    joinRequests: HomeJoinRequest[];
    birthdays: HomeBirthdayItem[];
    ok: HomeOkChips;
    unreadNotifications: number;
}
