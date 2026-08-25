// Типы зеркалят DTO бэкенда (System.Text.Json, минимальные API): свойства camelCase,
// enum'ы без JsonStringEnumConverter сериализуются как целые числа — см. FamilyHub.Domain.Enums.*.

export const FamilyRole = {Member: 0, Admin: 1} as const;

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
    displayName: string;
    username: string | null;
    role: number; // FamilyRole
    joinedAt: string;
}


export interface CurrentMember {
    id: string;
    displayName: string;
    username: string | null;
    role: number; // FamilyRole
    joinedAt: string;
}


export interface InviteCreated {
    id: string;
    code: string;
    maxUses: number;
    expiresAt: string | null;
    telegramLink: string | null;
}

/** Подопечный без своего User — ребёнок, питомец или пожилой родственник (семейный ресурс,
 * см. FamilyHub.Api.Features.Dependents). Не заводим фейковый User с синтетическим email. */
export interface FamilyDependent {
    id: string;
    familyId: string;
    name: string;
    birthDate: string | null;
    isPet: boolean;
    petSpecies: string | null;
    createdByUserId: string;
    createdAt: string;
}

export interface FamilyDependentInput {
    name: string;
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

export interface Birthday {
    id: string;
    familyId: string;
    personName: string;
    date: string; // DateOnly "yyyy-MM-dd"
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
    personName: string;
    recordDate: string;
    doctor: string | null;
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
}

export interface MedicalRecordInput {
    kind: MedicalRecordKind;
    personName: string;
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
    createdAt: string;
    completedAt: string | null;
}

export interface IndicatorDto {
    id: string;
    analyteKey: string;
    displayName: string;
    flag: number; // IndicatorFlag
    position: number;
    valueRaw: string;
    unit: string | null;
    refLowText: string | null;
    refHighText: string | null;
    refText: string | null;
    recordDate: string; // DateOnly "yyyy-MM-dd"
    medicalRecordId: string;
}

/** Одна точка истории показателя (GET /api/indicators/{analyteKey}) — для спарклайна. */
export interface IndicatorHistoryPoint {
    recordDate: string;
    valueRaw: string;
    /** Только если ValueRaw распарсился как число (invariant-culture) — иначе null (качественный результат). */
    valueNumericText: string | null;
    flag: number; // IndicatorFlag
    medicalRecordId: string;
}

/** Последнее значение по каждому показателю среди СВОИХ записей (GET /api/indicators). */
export interface MyIndicatorSummary {
    analyteKey: string;
    displayName: string;
    valueRaw: string;
    unit: string | null;
    flag: number; // IndicatorFlag
    lastRecordDate: string;
}

/** Заключение врача (Kind=DoctorVisit), GET /api/medical-records/{id}/conclusion — MedicalRecord.ExtractedDataJson. */
export interface VisitConclusion {
    diagnosis: string | null;
    recommendations: string | null;
    prescriptions: string | null;
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

/** Лимиты загрузки вложений (GET /api/attachments/limits) — настраиваются в env, см. AttachmentUploadOptions. */
export interface AttachmentLimits {
    maxFileSizeBytes: number;
    maxFilesPerRecord: number;
}
