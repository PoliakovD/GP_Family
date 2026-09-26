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
    MedicalDocumentExtractionFailed: 8,
    MedicationEnrichmentFailed: 9,
    MedicationDoseDue: 10,
    MedicationDoseMissed: 11,
    MedicationStockLow: 12,
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
    /** §5 плана «живой конвейер» — зеркало IndicatorDto.enrichmentPending, см.
     * FamilyHub.Modules.Medical.Medications.MedicationDto. */
    enrichmentPending: boolean;
    /** Живой обрывок "мысли" модели (план "живой поток мыслей") — см. IndicatorDto.enrichmentLiveText. */
    enrichmentLiveText: string | null;
    /** См. ActiveJobItem.queueAhead — позиция в общей очереди к LLM, не только конвейера обогащения препаратов. */
    enrichmentQueueAhead: number;
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
    /** Источник ВСЕЙ записи (заметка 1) — никогда не пустая строка, сентинел "не определено", если
     * ещё не резолвлен. Меняется отдельно через SetRecordSpecimenRequest/PUT .../specimen. */
    specimenKbId: string;
    specimenDisplayName: string | null;
    /** Обобщённое слово источника без локализации ("мазок"), которое модель увидела в документе,
     * но не смогла уточнить, откуда именно (заметка 2) — non-null означает "спросите у пользователя,
     * это ЕЩЁ НЕ сохранённый источник" (specimenKbId в этом случае остаётся сентинелом). */
    specimenHint: string | null;
    /** Батч-загрузка (record-batch-add) — вид записи выше провизорный (по вкладке, откуда
     * загружали), пока пайплайн не определит его документом (DocumentKindClassifier) и не снимет
     * флаг. Обычная форма создания («+ Добавить») никогда не выставляет этот флаг. */
    kindIsAutoDetected: boolean;
    /** Биоматериал введён вручную, пока ИИ был недоступен — проверка названия отложена, бэкенд
     * применит его к записи, когда сервер вернётся (см. MedicalRecord.PendingSpecimenText). */
    pendingSpecimenText: string | null;
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
    /** Батч-загрузка (record-batch-add) — kind выше провизорный, пайплайн определит вид документом
     * (см. MedicalRecord.KindIsAutoDetected). Опционально: обычная форма создания не передаёт его
     * вовсе, дефолт на бэкенде — false. */
    autoDetectKind?: boolean;
}

/** Зеркало FamilyHub.Domain.Enums.AttachmentPreviewStatus — числовые значения, не строки
 * (System.Text.Json сериализует enum по умолчанию как число). */
export const AttachmentPreviewStatus = { None: 0, Pending: 1, Ready: 2, Failed: 3, Unsupported: 4 } as const;
export type AttachmentPreviewStatus = (typeof AttachmentPreviewStatus)[keyof typeof AttachmentPreviewStatus];

export interface Attachment {
    id: string;
    fileName: string;
    contentType: string;
    sizeBytes: number;
    uploadedAt: string;
    /** Когда конвейер извлечения последний раз успешно распознал этот файл — null, если ещё
     * ни разу (v2: определяет, есть ли записи нечего распознавать кнопкой «Распознать»). */
    extractedAt: string | null;
    /** Статус фоновой генерации превью (AttachmentPreviewProcessor) — Pending показывает спиннер
     * «Готовим превью…» вместо похода за /preview, Unsupported/Failed сразу рисуют карточку
     * «Скачать» без лишнего запроса. */
    previewStatus: AttachmentPreviewStatus;
}

/** Зеркало FamilyHub.Modules.Medical.Attachments.AttachmentRenderKind — что вьюер должен
 * нарисовать; сервер уже решил это за клиента (какой артефакт есть, какой из них главный). */
export const AttachmentRenderKind = { None: 0, Pdf: 1, Image: 2, Text: 3 } as const;
export type AttachmentRenderKind = (typeof AttachmentRenderKind)[keyof typeof AttachmentRenderKind];

/** Ответ GET /api/attachments/{id}/preview — уже готовые подписанные ссылки (5 минут TTL,
 * не кэшировать между открытиями вьюера). */
export interface AttachmentPreview {
    status: AttachmentPreviewStatus;
    renderKind: AttachmentRenderKind;
    fileName: string;
    contentType: string;
    sizeBytes: number;
    pageCount: number | null;
    thumbnailUrl: string | null;
    contentUrl: string | null;
    downloadUrl: string;
    failureReason: string | null;
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

/** См. FamilyHub.Domain.Enums.NotificationRelatedKind — null у типов оповещений без устоявшегося
 * целевого экрана в рамках задачи (карточка остаётся некликабельной, см. openRelated). */
export const NotificationRelatedKind = {
    MedicalRecordAnalysis: 0,
    MedicalRecordVisit: 1,
    Medkit: 2,
    MedicationDose: 3,
    MedicationCourse: 4,
} as const;
export type NotificationRelatedKind = typeof NotificationRelatedKind[keyof typeof NotificationRelatedKind];

export interface AppNotification {
    id: string;
    type: number; // NotificationType
    title: string;
    body: string;
    relatedEntityId: string;
    relatedEntityKind: NotificationRelatedKind | null;
    createdAt: string;
    isRead: boolean;
    readAt: string | null;
}

/** Глобальный индикатор фоновых процессов (§4 плана «живой конвейер»), GET /api/jobs/active-summary
 * — см. FamilyHub.Api.Features.Jobs.UserJobsService. recordId/recordKind — null, когда цель уже не
 * существует (запись/медикамент удалены к моменту опроса) — строка тогда без навигации. */
export interface ActiveJobItem {
    jobId: string;
    label: string;
    recordId: string | null;
    recordKind: NotificationRelatedKind | null;
    createdAt: string;
    /** Живой обрывок "мысли" модели (план "живой поток мыслей") — non-null максимум у ОДНОЙ
     * строки из всех активных задач всей системы одновременно (LmStudioConcurrencyGate
     * сериализует все вызовы LM Studio) — у остальных Pending это просто null, они ждут очередь. */
    liveText: string | null;
    /** Распознавание ждёт, пока вернётся ИИ (LM Studio недоступен) — позиции в очереди у него нет. */
    waitingForAi: boolean;
    /** Сколько задач из ЛЮБОГО из четырёх конвейеров реально стоят раньше этой в общей очереди к
     * LLM (не только своего конвейера — "extraction"/"enrichment" делят одну модель) — 0 у той
     * самой строки, что реально держит гейт прямо сейчас (см. liveText выше). */
    queueAhead: number;
}

export interface ActiveJobsGroup {
    total: number;
    items: ActiveJobItem[];
}

export interface ActiveJobsSummaryResponse {
    extraction: ActiveJobsGroup;
    labAnalyte: ActiveJobsGroup;
    medication: ActiveJobsGroup;
    visitMedication: ActiveJobsGroup;
}

// Ветка medicalrecords (задачи 5.2/5.3): конвейер извлечения показателей анализов и заключений
// врача — см. FamilyHub.Modules.Medical.Extraction.

/** Итог сравнения показателя с референсным диапазоном (бланк приоритетнее справочника). */
export const IndicatorFlag = { Unknown: 0, Low: 1, Normal: 2, High: 3, Critical: 4 } as const;
export type IndicatorFlag = typeof IndicatorFlag[keyof typeof IndicatorFlag];

/** Откуда взят референс (v2, каскад приоритетов) — KbCalculated показывается на фронте
 * бэйджем «рассчитано ИИ», Inferred — бэйджем «норма от ИИ» (модель сама предположила норму по
 * общемедицинским знаниям, когда в бланке референса не было вовсе — план "нормы из бланка").
 * См. FamilyHub.Domain.Enums.RefSource. */
export const RefSource = { None: 0, Blank: 1, KbFixed: 2, KbCalculated: 3, Inferred: 4 } as const;
export type RefSource = typeof RefSource[keyof typeof RefSource];

// Источник показателя (пересборка enrich-пайплайна) — раньше фиксированный enum SpecimenType,
// теперь ссылка (specimenKbId) на общий справочник (GlobalSpecimenKb): биоматериал ("кровь",
// "моча") ИЛИ небиологическое исследование ("ЭКГ", "УЗИ") — одна и та же таблица на оба рода
// понятия, никакого списка значений на фронте не осталось. specimenDisplayName приходит от
// сервера готовой строкой — фронт её не переводит и не сопоставляет локально.

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
    /** Сколько задач из ЛЮБОГО из четырёх конвейеров (не только извлечения — показатели/
     * медикаменты делят с ним одну и ту же локальную LLM) реально стоят раньше этой в общей
     * очереди; осмысленна, пока status === Pending или Running (иначе всегда 0) — Running не
     * значит "модель прямо сейчас отвечает по этой задаче", только что Hangfire её уже взял. */
    queuePosition: number;
    /** Живой обрывок "мысли" модели (план "живой поток мыслей") — null между вызовами/на
     * security-гейтах/когда задача не Running. */
    currentThought: string | null;
    /** Задача Pending, но ИИ (LM Studio) сейчас недоступен: она не потеряна и стартует сама, когда
     * сервер вернётся — UI показывает «ждём ИИ» вместо позиции в очереди. */
    waitingForAi: boolean;
}

/** Тело ответа POST /extract на "мягких" исходах (см. ExtractionRequestResult на бэкенде) — на
 * успехе (202 Accepted) тело пустое, эти поля отсутствуют. */
export interface ExtractionRequestResponse {
    /** waiting_for_ai (202) — задача создана, но ИИ недоступен: ждёт в очереди и запустится сама. */
    code?: 'already_queued' | 'waiting_for_ai';
    message?: string;
}

export interface IndicatorDto {
    id: string;
    analyteKey: string;
    displayName: string;
    flag: number; // IndicatorFlag
    refSource: number; // RefSource
    specimenKbId: string;
    /** Готовое отображаемое имя источника — резолвится сервером, null, если ссылка почему-то
     * не нашлась (не должно случаться). */
    specimenDisplayName: string | null;
    position: number;
    valueRaw: string;
    unit: string | null;
    refLowText: string | null;
    refHighText: string | null;
    refText: string | null;
    recordDate: string; // DateOnly "yyyy-MM-dd"
    medicalRecordId: string;
    /** Редизайн v2 — invariant-culture double либо null (качественный результат без числа).
     * Гарантия та же, что у refLowText/refHighText: parseFloat без нормализации запятых. */
    valueNumericText: string | null;
    /** Редизайн v2 — ключ статьи справочника показателей; null, пока обогащение до него не дошло. */
    kbAnalyteId: string | null;
    /** Имя как оно было напечатано в бланке (очищено только от нумерации/эха, не от регистра
     * справочника) — заполнено, только когда отличается от displayName (пересборка enrich-пайплайна:
     * канон справочника подставляется в displayName при попадании). Подсказка "в бланке: …" в UI. */
    rawDisplayName: string | null;
    /** §5 плана «живой конвейер» — промах по справочнику, обогащение ещё не завершилось (см.
     * FamilyHub.Modules.Medical.Extraction.ExtractionQueryDtos.IndicatorDto). UI показывает чип
     * «уточняем норму…» вместо того, чтобы молча остаться без нормы навсегда. */
    enrichmentPending: boolean;
    /** Живой обрывок "мысли" модели (план "живой поток мыслей") — null почти всегда, даже когда
     * enrichmentPending===true: непусто только пока эта конкретная задача реально держит гейт LM
     * Studio, не просто ждёт очередь (см. class doc ActiveJobItem.LiveText). */
    enrichmentLiveText: string | null;
    /** См. ActiveJobItem.queueAhead — позиция в общей очереди к LLM, не только конвейера обогащения показателей. */
    enrichmentQueueAhead: number;
    /** Обогащение остановилось из-за недоступного ИИ и продолжится само, когда он вернётся. */
    enrichmentWaitingForAi: boolean;
}

/** Ручная правка показателя (ошибка OCR), PUT /api/indicators/{id} — все поля целиком, не патч.
 * specimenKbId сюда не входит (заметка 1) — источник теперь атрибут ВСЕЙ записи, меняется
 * отдельно через SetRecordSpecimenRequest/PUT .../specimen. */
export interface UpdateIndicatorRequest {
    displayName: string;
    valueRaw: string;
    unit: string | null;
    refLowText: string | null;
    refHighText: string | null;
    refText: string | null;
}

/** Ручное добавление показателя, POST /api/medical-records/{recordId}/indicators — та же форма,
 * что UpdateIndicatorRequest. */
export type CreateIndicatorRequest = UpdateIndicatorRequest;

/** Ручная смена/уточнение источника ВСЕЙ записи (заметка 1), PUT /api/medical-records/{id}/specimen —
 * каскадится на все показатели записи (см. ExtractionQueryService.SetRecordSpecimenAsync). */
export interface SetRecordSpecimenRequest {
    specimenKbId: string;
}

/** Одна точка истории показателя (GET /api/indicators/{analyteKey}?specimen=&customId=) — для спарклайна. */
export interface IndicatorHistoryPoint {
    recordDate: string;
    valueRaw: string;
    /** Только если ValueRaw распарсился как число (invariant-culture) — иначе null (качественный результат). */
    valueNumericText: string | null;
    flag: number; // IndicatorFlag
    medicalRecordId: string;
}

/** Последнее значение по каждому (показателю, источнику, ПАЦИЕНТУ) среди СВОИХ записей
 * (GET /api/indicators) — familyDependentId/targetUserId оба null означает "Я"; передавать их же
 * в GET /api/indicators/{analyteKey}, чтобы история строилась по тому же человеку, не смешивала
 * показатели разных членов семьи (см. class doc LabIndicator.FamilyDependentId на бэкенде). */
export interface MyIndicatorSummary {
    analyteKey: string;
    displayName: string;
    specimenKbId: string;
    specimenDisplayName: string | null;
    valueRaw: string;
    unit: string | null;
    flag: number; // IndicatorFlag
    lastRecordDate: string;
    familyDependentId: string | null;
    targetUserId: string | null;
    patientName: string;
}

/** Один источник в результате поиска по общему справочнику (GET /api/specimens/search) —
 * заменяет прежний захардкоженный список 6 значений SpecimenType (пересборка enrich-пайплайна). */
export interface GlobalSpecimenDto {
    id: string;
    displayName: string;
}

/** "Недавно использованный этим пользователем" источник (GET /api/specimens) — что автоподсказка
 * должна предложить в первую очередь; сам справочник источников общий (GlobalSpecimenDto). */
export interface UserSpecimen {
    specimenKbId: string;
    displayName: string;
    lastUsedAt: string;
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
    /** Резюме устарело после ручной правки и пересчитывается в фоне (RecordSummaryRegenerationJob) —
     * показываем «Обновляем резюме…» и опрашиваем эндпоинт, пока не появится актуальный текст. */
    pending?: boolean;
}

// Редизайн v2 — справочник показателей анализов (GET /api/kb/analytes[/{id}]), зеркало
// KbListItem/KbMedicationCard выше на другую таблицу (kb.global_lab_analytes_kb).

export interface KbAnalyteListItem {
    id: string;
    displayName: string;
    specimenKbId: string; // ключ справочника (показатель, источник)
    specimenDisplayName: string | null;
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
    specimenKbId: string;
    specimenDisplayName: string | null;
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

/** Лимиты конвейера распознавания на пользователя (GET /api/medical-records/extraction-limits) —
 * см. ExtractionLimitsOptions. usedToday/activeNow — снимок на момент запроса, реальный расход
 * может обогнать его между чтением формой и постановкой в очередь (мягкий лимит, см. class doc
 * ExtractionRequestService на бэкенде). resetsAt — начало следующих суток UTC. */
export interface ExtractionLimits {
    maxBatchDocuments: number;
    maxActiveJobs: number;
    activeNow: number;
    dailyQuota: number;
    usedToday: number;
    resetsAt: string;
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
    medicationsExpired: number;
    medicationsExpiring: number;
    analysesTotal: number;
    analysesAbnormal: number;
    visitsTotal: number;
    visitsLastDate: string | null; // DateOnly "yyyy-MM-dd"
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

// ============================================================================
// Дневник самочувствия (HealthNote) — строго личные записи, см. HealthNoteService.
// ============================================================================

/** Значения — часть контракта с бэкендом (HealthNoteKind), не переупорядочивать. */
export const HealthNoteKind = {
    Symptom: 0, Metric: 1, Wellbeing: 2, MedicationIntake: 3, Sleep: 4, Note: 5,
} as const;
export type HealthNoteKind = typeof HealthNoteKind[keyof typeof HealthNoteKind];

export interface SymptomData {
    severity: number;
    areas?: string[] | null;
    detail?: string | null;
}

export interface MetricData {
    code: string;
    value: number;
    /** Нижнее давление — только у составных замеров. */
    value2?: number | null;
}

export interface WellbeingData {
    score: number;
    factors?: string[] | null;
}

export interface MedicationIntakeData {
    dose?: string | null;
}

export interface SleepData {
    bedTime: string;
    wakeTime: string;
    quality: number;
}

export interface HealthNote {
    id: string;
    kind: HealthNoteKind;
    occurredAt: string;
    title: string | null;
    text: string | null;
    includeInDoctorQuestions: boolean;
    symptom: SymptomData | null;
    metric: MetricData | null;
    wellbeing: WellbeingData | null;
    intake: MedicationIntakeData | null;
    sleep: SleepData | null;
    updatedAt: string;
}

export interface HealthNoteInput {
    kind: HealthNoteKind;
    occurredAt: string;
    title?: string | null;
    text?: string | null;
    includeInDoctorQuestions?: boolean;
    symptom?: SymptomData | null;
    metric?: MetricData | null;
    wellbeing?: WellbeingData | null;
    intake?: MedicationIntakeData | null;
    sleep?: SleepData | null;
}

export interface HealthMetricDefinition {
    code: string;
    name: string;
    unit: string;
    min: number;
    max: number;
    hasSecondValue: boolean;
    min2: number | null;
    max2: number | null;
}

export interface HealthNoteCatalog {
    metrics: HealthMetricDefinition[];
    bodyAreas: string[];
    wellbeingFactors: string[];
}

export interface HealthMetricPoint {
    occurredAt: string;
    value: number;
    value2: number | null;
}

// ============================================================================
// Отчёт для врача (DoctorReport) — PDF-снимок данных пациента + публичная ссылка.
// ============================================================================

/** Значения — часть контракта с бэкендом (DoctorReportLinkStatus). */
export const DoctorReportLinkStatus = {None: 0, Active: 1, Expired: 2, Revoked: 3} as const;
export type DoctorReportLinkStatus = typeof DoctorReportLinkStatus[keyof typeof DoctorReportLinkStatus];

export interface DoctorReportBlocks {
    labs: boolean;
    aiSummaries: boolean;
    medications: boolean;
    visits: boolean;
    measurements: boolean;
    symptomsNotes: boolean;
}

export interface DoctorReportLink {
    status: DoctorReportLinkStatus;
    /** Токен ссылки; адрес строится как {origin}/r/{token}. Есть у активной и истёкшей ссылки. */
    token: string | null;
    expiresAt: string | null;
    revokedAt: string | null;
    viewCount: number;
    lastViewedAt: string | null;
}

export interface DoctorReport {
    id: string;
    periodFrom: string;
    periodTo: string;
    createdAt: string;
    pageCount: number;
    blockCount: number;
    recipient: string | null;
    blocks: DoctorReportBlocks;
    link: DoctorReportLink;
}

export interface CreateDoctorReportRequest {
    periodFrom: string;
    periodTo: string;
    includeLabs: boolean;
    includeAiSummaries: boolean;
    includeMedications: boolean;
    includeVisits: boolean;
    includeMeasurements: boolean;
    includeSymptomsNotes: boolean;
    recipient: string | null;
    patientComment: string | null;
    /** 7, 14 или 30 — сразу выпустить ссылку; null — только PDF. */
    shareDays: number | null;
}

export interface DoctorReportCounts {
    analyses: number;
    visits: number;
    diaryEntries: number;
    /** Заметки дневника с пометкой «в вопросы к врачу» — попадут в блок жалоб. */
    flaggedNotes: number;
}

/** Что видит врач на публичной странице до открытия PDF. */
export interface PublicReportMeta {
    patientName: string;
    sex: string | null;
    age: number | null;
    birthDate: string | null;
    periodFrom: string;
    periodTo: string;
    createdAt: string;
    expiresAt: string;
    pageCount: number;
    sections: string[];
}

// ============================================================================
// Приём лекарств (MedicationCourse) — курсы, приёмы, «Сегодня», напоминания (ADR-0015).
// Значения enum'ов — часть контракта с бэкендом, не переупорядочивать. Время — «HH:mm:ss»
// (TimeOnly), даты — «yyyy-MM-dd» (DateOnly), моменты — ISO UTC.
// ============================================================================

export const DoseScheduleMode = {
    TimesPerDay: 0, EveryNHours: 1, Weekdays: 2, Cycle: 3, AsNeeded: 4,
} as const;
export type DoseScheduleMode = typeof DoseScheduleMode[keyof typeof DoseScheduleMode];

export const FoodRelation = {Any: 0, Before: 1, With: 2, After: 3} as const;
export type FoodRelation = typeof FoodRelation[keyof typeof FoodRelation];

export const DoseUnit = {Tablet: 0, Capsule: 1, Ml: 2, Drop: 3, Sachet: 4, Dose: 5} as const;
export type DoseUnit = typeof DoseUnit[keyof typeof DoseUnit];

export const DoseStatus = {Pending: 0, Snoozed: 1, Taken: 2, Skipped: 3, Missed: 4} as const;
export type DoseStatus = typeof DoseStatus[keyof typeof DoseStatus];

export const DoseAction = {Taken: 0, Snooze10: 1, Snooze30: 2, Skip: 3} as const;
export type DoseAction = typeof DoseAction[keyof typeof DoseAction];

/** Итог приёма для сетки истории и «Сегодня»: вовремя / с опозданием / пропущен / впереди. */
export const DoseOutcome = {
    Upcoming: 0, Due: 1, OnTime: 2, Late: 3, Missed: 4, Skipped: 5,
} as const;
export type DoseOutcome = typeof DoseOutcome[keyof typeof DoseOutcome];

export const CourseStatus = {Active: 0, Paused: 1, Completed: 2} as const;
export type CourseStatus = typeof CourseStatus[keyof typeof CourseStatus];

export const DayPeriod = {Morning: 0, Day: 1, Evening: 2} as const;
export type DayPeriod = typeof DayPeriod[keyof typeof DayPeriod];

export interface DoseTime {
    /** «HH:mm:ss» */
    at: string;
    units: number;
}

export interface DoseSchedule {
    mode: DoseScheduleMode;
    times?: DoseTime[] | null;
    intervalHours?: number | null;
    intervalStart?: string | null;
    intervalUnits?: number | null;
    /** DayOfWeek: 0 — воскресенье … 6 — суббота (как в .NET и JS Date.getDay()). */
    weekdays?: number[] | null;
    cycleOnDays?: number | null;
    cycleOffDays?: number | null;
    maxPerDay?: number | null;
}

/** Кто принимает: kind — «user» (аккаунт) или «dependent» (подопечный); isSelf — это я. */
export interface IntakeSubject {
    kind: 'user' | 'dependent';
    id: string;
    name: string;
    isSelf: boolean;
}

export interface CourseStock {
    medicationId: string;
    medicationName: string;
    medkitName: string | null;
    quantityText: string | null;
    quantity: number | null;
    daysCovered: number | null;
    neededForCourse: number | null;
    shortfall: number | null;
}

export interface CourseSummary {
    id: string;
    drugName: string;
    subject: IntakeSubject;
    status: CourseStatus;
    schedule: DoseSchedule;
    food: FoodRelation;
    unit: DoseUnit;
    startDate: string;
    endDate: string | null;
    dayNumber: number;
    totalDays: number | null;
    canEdit: boolean;
    isWatching: boolean;
    missedToday: boolean;
    writeOffEnabled: boolean;
    stock: CourseStock | null;
}

export interface CourseSource {
    recordId: string;
    doctor: string | null;
    recordDate: string;
}

export interface Adherence {
    onTime: number;
    counted: number;
    percent: number | null;
}

export interface CourseWatcher {
    userId: string;
    name: string;
    receiveReminders: boolean;
    notifyMissed: boolean;
}

export interface CourseDetail {
    summary: CourseSummary;
    notes: string | null;
    prescriptionText: string | null;
    source: CourseSource | null;
    medicationId: string | null;
    repeatAfterMinutes: number | null;
    missedAfterMinutes: number;
    lowStockDays: number;
    timeZoneId: string;
    nextBreakStart: string | null;
    adherence: Adherence;
    watchers: CourseWatcher[];
    canDelete: boolean;
}

export interface CourseRequest {
    dependentId: string | null;
    drugName: string;
    schedule: DoseSchedule;
    food: FoodRelation;
    unit: DoseUnit;
    startDate: string;
    endDate: string | null;
    medicationId: string | null;
    writeOff: boolean;
    repeatAfterMinutes: number | null;
    missedAfterMinutes: number;
    lowStockDays: number;
    sourceMedicalRecordId: string | null;
    sourcePrescriptionIndex: number | null;
    prescriptionText: string | null;
    notes: string | null;
}

export interface CoursePreviewRequest {
    schedule: DoseSchedule;
    startDate: string;
    endDate: string | null;
    unit: DoseUnit;
    medicationId: string | null;
}

export interface CoursePreview {
    averageUnitsPerDay: number;
    neededForCourse: number | null;
    nextBreakStart: string | null;
    medicationName: string | null;
    quantityText: string | null;
    quantity: number | null;
    daysCovered: number | null;
    shortfall: number | null;
}

export interface HistoryCell {
    date: string;
    time: string;
    scheduledAt: string;
    outcome: DoseOutcome;
    doseId: string | null;
}

export interface CourseHistory {
    from: string;
    to: string;
    cells: HistoryCell[];
    adherence: Adherence;
}

/** Черновик полей формы, угаданный из текста назначения (частичный: что не распознано — null). */
export interface PrescriptionDraft {
    mode: DoseScheduleMode | null;
    timesPerDay: number | null;
    intervalHours: number | null;
    units: number | null;
    unit: DoseUnit | null;
    durationDays: number | null;
    food: FoodRelation | null;
}

export interface PrescriptionItem {
    index: number;
    name: string;
    dosageInstructions: string | null;
    draft: PrescriptionDraft;
}

export interface PrescriptionVisit {
    recordId: string;
    recordDate: string;
    doctor: string | null;
    title: string | null;
    dependentId: string | null;
    items: PrescriptionItem[];
}

export interface DoseResult {
    id: string;
    status: DoseStatus;
    scheduledAt: string | null;
    takenAt: string | null;
    snoozedUntil: string | null;
    units: number;
    stockWrittenOff: boolean;
}

export interface TodayCounters {
    taken: number;
    missed: number;
    upcoming: number;
    skipped: number;
    total: number;
    nextAt: string | null;
}

export interface TodayDose {
    courseId: string;
    doseId: string | null;
    scheduledAt: string;
    localTime: string;
    period: DayPeriod;
    drugName: string;
    units: number;
    unit: DoseUnit;
    food: FoodRelation;
    subject: IntakeSubject;
    outcome: DoseOutcome;
    takenAt: string | null;
    snoozedUntil: string | null;
    dayNumber: number;
    totalDays: number | null;
    canAct: boolean;
    isWatching: boolean;
}

export interface TodayAsNeeded {
    courseId: string;
    drugName: string;
    subject: IntakeSubject;
    maxPerDay: number;
    takenToday: number;
    units: number;
    unit: DoseUnit;
    canAct: boolean;
}

export interface FamilyAlert {
    courseId: string;
    doseId: string | null;
    subject: IntakeSubject;
    drugName: string;
    scheduledAt: string;
    localTime: string;
}

export interface LowStock {
    courseId: string;
    drugName: string;
    quantityText: string | null;
    daysCovered: number;
    lowStockDays: number;
    courseDaysLeft: number | null;
}

export interface WeekDay {
    date: string;
    onTime: number;
    late: number;
    missed: number;
    skipped: number;
    upcoming: number;
    isToday: boolean;
}

export interface IntakeToday {
    date: string;
    timeZoneId: string;
    counters: TodayCounters;
    subjects: IntakeSubject[];
    items: TodayDose[];
    asNeeded: TodayAsNeeded[];
    alerts: FamilyAlert[];
    lowStock: LowStock[];
    week: WeekDay[];
    weekOnTimePercent: number | null;
}

export interface WatcherCandidate {
    userId: string;
    name: string;
    enabled: boolean;
}

export interface WatchingEntry {
    kind: 'user' | 'dependent';
    id: string;
    name: string;
    courseCount: number;
    isWatching: boolean;
    notifyMissed: boolean;
    receiveReminders: boolean;
}

export interface ReminderSettings {
    timeZoneId: string | null;
    /** «HH:mm:ss» или null — тихие часы выключены. */
    quietHoursFrom: string | null;
    quietHoursTo: string | null;
    myWatchers: WatcherCandidate[];
    watching: WatchingEntry[];
}
