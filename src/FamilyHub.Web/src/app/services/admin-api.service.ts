import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { ApiError } from './api.service';
import { DevLoggerService } from './dev-logger.service';

export interface UsersOverview {
  total: number; telegramOnly: number; pwaOnly: number; both: number;
  newLast7Days: number; newLast30Days: number; lockedOut: number;
}
export interface FamiliesOverview { total: number; withActiveMembers: number; averageActiveMembers: number; }
export interface DomainCounts {
  medicalRecords: number; medications: number; expiredMedications: number;
  birthdays: number; attachments: number; familyDependents: number;
}
export interface AdminOverview { users: UsersOverview; families: FamiliesOverview; domain: DomainCounts; }

export interface StorageReconciliation { orphanedBlobs: number; brokenAttachments: number; }
export interface AdminStorageStats {
  bucketSizeBytes: number; bucketObjectCount: number;
  attachmentsSizeBytesInDb: number; attachmentsCountInDb: number;
  reconciliation: StorageReconciliation; computedAt: string;
}

export interface OutboxBacklog { undeliveredBatches: number; oldestUndeliveredAt: string | null; }
export interface HangfireQueue { name: string; enqueuedCount: number; }
export interface AdminSystemStats {
  outbox: OutboxBacklog; hangfireQueues: HangfireQueue[]; hangfireFailedJobsTotal: number;
  postgresHealthy: boolean; minioHealthy: boolean; kafkaHealthy: boolean;
}

export interface KeyIdCount { keyId: string; count: number; }
export interface EncryptionKeyDistribution { fieldValues: KeyIdCount[]; attachmentBlobs: KeyIdCount[]; }
export interface AdminSecurityStats {
  encryptionDistribution: EncryptionKeyDistribution;
  crossUserMedicalAccessLast30Days: number;
  usersWithoutCurrentConsent: number;
  activeSessions: number;
  dataProtectionKeyCount: number;
  oldestDataProtectionKeyCreatedAt: string | null;
}

export interface AdminKeyRings {
  encryption: { activeKeyId: string; previousKeyIds: string[] };
  jwt: { activeKeyId: string; previousKeyIds: string[] };
  attachments: { previousKeyCount: number };
}

/** Откуда взято значение настройки: env / appsettings / cli / other, либо default — нигде не задано,
 * действует значение по умолчанию из кода. */
export type ConfigSource = 'env' | 'appsettings' | 'cli' | 'other' | 'default';

/** Одна настройка «Настройки → Конфиг env». Секрет (isSecret) значения не несёт вообще — value всегда
 * null, есть только isSet («задан / не задан»); так устроен и бэкенд (AdminConfigService). */
export interface ConfigItem { key: string; value: string | null; isSecret: boolean; isSet: boolean; source: ConfigSource; }
export interface ConfigSection { name: string; title: string; items: ConfigItem[]; }
export interface AdminConfig { sections: ConfigSection[]; }

// --- Ротация учёток приложения к Postgres/MinIO (ADR-0011) ---

export type CredentialKind = 'Postgres' | 'Minio';
export type CredentialRotationStatus = 'AwaitingDeploy' | 'Activated' | 'Revoked' | 'Superseded';

export interface CredentialSlot { role: string; canLogin: boolean; hasPassword: boolean; activeSessions: number; }

/** Запись истории ротации — только метаданные, секретов нет. У MinIO from/to — ключи в маске. */
export interface CredentialRotation {
  id: string; kind: CredentialKind; from: string; to: string; status: CredentialRotationStatus;
  generatedAt: string; generatedBy: string; activatedAt: string | null; revokedAt: string | null;
  terminatedSessions: number | null;
}

/** mode: LeastPrivilege — ротация доступна; Superuser — приложение под суперпользователем (dev либо
 * не выполнена первичная настройка); Unavailable — Postgres не ответил. */
export interface PostgresCredentialStatus {
  mode: 'LeastPrivilege' | 'Superuser' | 'Unavailable'; sessionUser: string | null; slots: CredentialSlot[];
  currentSince: string | null; pending: CredentialRotation | null; revocableOld: string | null;
}

/** mode: ServiceAccount — ротация доступна; NotServiceAccount — под root/пользователем; Unknown — MinIO не ответил. */
export interface MinioCredentialStatus {
  mode: 'ServiceAccount' | 'NotServiceAccount' | 'Unknown'; accessKeyMasked: string; currentSince: string | null;
  pending: CredentialRotation | null; revocableOldMasked: string | null;
}

export interface CredentialsStatus { postgres: PostgresCredentialStatus; minio: MinioCredentialStatus; history: CredentialRotation[]; }

/** Ответ «Сгенерировать»: строки для PROD_ENV, показываются ОДИН раз и нигде не сохраняются. */
export interface GeneratedCredential { rotationId: string; envLines: string[]; }
export interface RevokedCredential { terminatedSessions: number | null; }

export interface RotationStatus {
  runId: string | null; targetKeyId: string | null; status: string | null;
  startedAt: string | null; finishedAt: string | null; lastError: string | null;
  fieldsProcessed: number; fieldsTotal: number; blobsProcessed: number; blobsTotal: number;
}

/** WebSearchTopic (см. FamilyHub.Domain.Enums) — 0=Medication, 1=LabAnalyte. Тело JSON-запросов не
 * настроено на JsonStringEnumConverter (см. AdminEnrichmentEndpoints), поэтому enum'ы — числами,
 * тот же формат, что и остальные enum-поля запросов в проекте (см. InviteCreated.assignedRole). */
export const WebSearchTopic = { Medication: 0, LabAnalyte: 1 } as const;
export type WebSearchTopicValue = (typeof WebSearchTopic)[keyof typeof WebSearchTopic];

export interface TrustedDomain { id: string; domain: string; rank: number; isEnabled: boolean; }

export interface SearchCacheRow {
  id: string; normalizedName: string; specimen: string | null; provider: string;
  lastUpdatedAt: string; canBeUpdatedAfter: string; snippetCount: number;
}
export interface SearchCacheListResponse { rows: SearchCacheRow[]; total: number; }

export interface SearchCacheSnippet {
  title: string; url: string; text: string; domain: string | null;
  isTrustedByDomain: boolean; override: boolean | null; enabled: boolean;
}
export interface SearchCacheDetail {
  id: string; normalizedName: string; specimen: string | null; provider: string;
  lastUpdatedAt: string; canBeUpdatedAfter: string; snippets: SearchCacheSnippet[];
}

/** Черновик правки сниппета — только то, что реально редактируется (без вычисленных
 * enabled/isTrustedByDomain — это read-only проекция сервера). */
export interface SearchCacheSnippetInput { title: string; url: string; text: string; }

/** Полное редактирование строки кэша (§ CRUD кэша) — snippets заменяет список целиком: добавить =
 * включить новую запись, отредактировать = поменять поля существующей, убрать = не включить в
 * список. NormalizedName/SpecimenKbId не редактируются — см. class doc UpdateSearchCacheRequest
 * на бэкенде (бизнес-ключ, по которому задачи ищут строку). */
export interface UpdateSearchCacheRequest {
  topic: WebSearchTopicValue; provider?: string | null; snippets: SearchCacheSnippetInput[];
}

/** WebSearchCallOutcome (см. FamilyHub.Domain.Enums) — тот же приём числового enum'а, что
 * WebSearchTopic выше. CacheHit — единственное НЕ платное значение (см. class doc на бэкенде). */
export const WebSearchCallOutcome = {
  Ok: 0, Empty: 1, Rejected: 2, NoUsedSources: 3, HttpError: 4, Timeout: 5, CacheHit: 6,
} as const;
export type WebSearchCallOutcomeValue = (typeof WebSearchCallOutcome)[keyof typeof WebSearchCallOutcome];

/** Аудит-лог платных вызовов внешнего веб-поиска (см. AdminSearchCallsEndpoints) — страница
 * «Операции → Журнал вызовов». */
export interface SearchCallRow {
  id: string; occurredAt: string; provider: string; topic: WebSearchTopicValue; normalizedName: string;
  specimenDisplayName: string | null; httpStatus: number | null; durationMs: number;
  outcome: WebSearchCallOutcomeValue; snippetCount: number; jobKind: string | null; jobId: string | null;
}
export interface SearchCallListResponse { rows: SearchCallRow[]; total: number; page: number; pageSize: number; }

export interface SearchCallDetail extends SearchCallRow {
  queryText: string; endpoint: string | null; resultUrls: string[]; error: string | null;
}

export interface SearchCallCountByKey { key: string; count: number; }
export interface SearchCallDailyCount { day: string; paidCalls: number; cacheHits: number; }
/** Квоты больше нет (ADR-0005 §9, замена вентилем) — webSearchPaused/pausedAt отражают текущее
 * состояние IWebSearchValveService, estimatedMonthlySpend = usedThisMonth * PricePerPaidCall
 * (null, если цена вызова не задана в конфиге). */
export interface SearchCallStats {
  totalCalls: number; paidCalls: number; cacheHits: number; cacheHitShare: number;
  byProvider: SearchCallCountByKey[]; byOutcome: SearchCallCountByKey[]; byDay: SearchCallDailyCount[];
  usedThisMonth: number; webSearchPaused: boolean; pausedAt: string | null; estimatedMonthlySpend: number | null;
}

/** Вентиль платного поиска (ADR-0005 §9) — GET/PUT /api/admin/enrichment/web-search. */
export interface WebSearchValve { isPaused: boolean; pausedAt: string | null; note: string | null; }

/** Прогон пересборки справочника показателей (пересборка enrich-пайплайна, §4.2 плана) — зеркало
 * RotationStatus на LabAnalyteKbRebuildJob. status: "Running" | "Completed" | "Failed" | null. */
export interface KbRebuildStatus {
  runId: string | null; status: string | null; startedAt: string | null; finishedAt: string | null;
  lastError: string | null; stageIndex: number;
  cacheMerged: number; indicatorsUpdated: number; indicatorsMerged: number;
  catalogDeleted: number; reseedRequested: number;
}

/** Прогон прогрева кэша веб-поиска из админки (грантовый лимит облака) — зеркало KbRebuildStatus
 * на SearchCacheWarmupJob. status: "Running" | "Paused" | "Completed" | "Failed" | "Cancelled" | null
 * (ни разу не запускался). specimenDisplayName — только для topic=LabAnalyte. */
export interface WarmupStatus {
  runId: string | null; status: string | null; topic: WebSearchTopicValue | null;
  specimenDisplayName: string | null; totalNames: number; cursor: number; paidCalls: number;
  skippedKbHit: number; skippedFreshCache: number; failures: number; maxPaidCalls: number | null;
  startedAt: string | null; finishedAt: string | null; lastError: string | null;
}

export interface StartWarmupRequest {
  topic: WebSearchTopicValue; specimenKbId?: string | null; names: string; maxPaidCalls?: number | null;
}

/** Один шаг одного enrich-пайплайна (управление пайплайном из админки, §2 плана) — реальный
 * порядок вызовов зашит в коде (жёсткие зависимости между шагами одного прогона), из админки
 * доступно только вкл/выкл необязательных шагов, не реордер. */
export interface PipelineStep {
  pipelineKey: string; stepKey: string; description: string;
  isMandatory: boolean; isEnabled: boolean; promptKey: string | null;
}

/** Слот промпта — activeVersion=null означает, что в БД нет активной версии и конвейер использует
 * захардкоженный фолбэк в коде (см. PromptProvider на бэкенде). */
export interface PromptSlot { key: string; description: string; activeVersion: number | null; activeVersionCreatedAt: string | null; }

export interface PromptVersion { id: string; version: number; isActive: boolean; note: string | null; createdAt: string; body: string; }

// --- Ручная правка справочников после ИИ (§3 плана) ---

export interface KbAnalyteListItem { id: string; displayName: string; specimenKbId: string; specimenDisplayName: string | null; plainExplanation: string | null; }
export interface KbAnalyteListResponse { items: KbAnalyteListItem[]; hasMore: boolean; }

export interface KbListItem { id: string; displayName: string; purpose: string | null; }
export interface KbListResponse { items: KbListItem[]; hasMore: boolean; }

/** LockedFields — подмножество {"displayName","payload","aliases"}; залоченное поле переживает
 * следующее автообогащение (см. AdminCatalogService на бэкенде). */
export interface AdminLabAnalyteDetail {
  id: string; normalizedName: string; specimenKbId: string; specimenDisplayName: string | null;
  displayName: string; payloadJson: string; source: string; aliases: string[]; lockedFields: string[];
  payloadVersion: number; createdAt: string; updatedAt: string;
}
export interface AdminMedicationDetail {
  id: string; normalizedName: string; displayName: string; payloadJson: string; source: string;
  aliases: string[]; lockedFields: string[]; payloadVersion: number; createdAt: string; updatedAt: string;
}

/** lockedPayloadKeys — режим формы (§4/§10 плана): если задан вместе с payloadJson, лочатся
 * отдельные "payload.<key>" (реально изменённые поля формы), а не весь "payload" целиком, как при
 * отсутствии этого поля (режим сырого JSON). */
export interface AdminKbEditRequest {
  displayName?: string | null; payloadJson?: string | null; aliases?: string[] | null;
  lockedPayloadKeys?: string[] | null;
}

export interface GlobalSpecimen { id: string; displayName: string; }

/** Итог резолва одного related-имени по точному NormalizedName — id/displayName/specimenDisplayName
 * все null, если статьи с таким именем в справочнике ещё нет (оборванная ссылка/опечатка, не ошибка). */
export interface AdminRelatedAnalyteMatch {
  name: string; id: string | null; displayName: string | null; specimenDisplayName: string | null;
}

export interface DryRunResponse { success: boolean; error: string | null; payload: Record<string, unknown> | null; }

export type PipelineJobType = 'lab-analyte' | 'medication' | 'visit-medication' | 'extraction';

/** См. FamilyHub.Domain.Enums.EnrichmentFailureReason — строкой (как Status), не числом (тот же
 * приём, что PipelineJob.status). null — задача ни разу не падала. */
export type EnrichmentFailureReasonValue =
  | 'Legitimacy' | 'Plausibility' | 'NoTrustedSnippets' | 'NoSourcesCited' | 'SummarizerFailed'
  | 'IsolationViolation' | 'LmStudioUnavailable' | 'ProviderFailed' | 'Unknown';

export interface PipelineJob {
  id: string; type: PipelineJobType; displayName: string; status: string; attempts: number;
  error: string | null; createdAt: string; startedAt: string | null; completedAt: string | null;
  failureReason: EnrichmentFailureReasonValue | null;
}
export interface PipelineJobListResponse { rows: PipelineJob[]; total: number; }

/** Карточка одной задачи для боковой панели — то же, что PipelineJob, плюс всё нужное, чтобы
 * разобрать и починить отказ, не переходя на другую вкладку (см. план, Context). Поля-специфика
 * lab-analyte (specimenKbId/specimenDisplayName/origin/force) — null/false для остальных типов;
 * searchCache — null, пока по этому имени не было ни одного платного поиска, либо для extraction
 * (у него нет понятия сниппетов). */
export interface PipelineJobDetail {
  id: string; type: PipelineJobType; displayName: string; status: string; attempts: number;
  error: string | null; failureReason: EnrichmentFailureReasonValue | null;
  createdAt: string; startedAt: string | null; completedAt: string | null;
  normalizedName: string | null; specimenKbId: string | null; specimenDisplayName: string | null;
  origin: string | null; force: boolean; provider: string | null; externalSearchAt: string | null;
  isTransientFailure: boolean; kbId: string | null;
  searchCache: SearchCacheDetail | null; trustedDomains: TrustedDomain[];
}

export interface SnippetOverrideItem { url: string; enabled: boolean | null; }

/** «Применить и перезапустить» из карточки задачи — оба поля необязательны и независимы. */
export interface ResolveAndRetryRequest { overrides?: SnippetOverrideItem[]; trustDomains?: string[]; }

export interface BulkRetryResponse { retriedCount: number; notFoundIds: string[]; }
export interface BulkDeleteResponse { deletedCount: number; notFoundIds: string[]; }

/** Итог очистки задач, упавших до появления структурной причины (FailureReason=null,
 * «Unclassified» в инбоксе «Требует внимания») — по конвейеру и общий. */
export interface PurgeUnclassifiedResponse {
  labAnalyteDeleted: number; medicationDeleted: number; visitMedicationDeleted: number;
  extractionDeleted: number; totalDeleted: number;
}

/** Итог dedupe-failed — extraction не участвует, у неё нет понятия "то же название" (ключ
 * дедупа — MedicalRecordId, уникален по построению). */
export interface DedupeFailedResponse {
  labAnalyteDeleted: number; medicationDeleted: number; visitMedicationDeleted: number; totalDeleted: number;
}

/** Один пункт инбокса «Требует внимания» — агрегат причин отказа по всем четырём конвейерам.
 * count — сырое число Failed-строк (может включать дубли одного названия); distinctCount —
 * сколько РАЗНЫХ названий(+биоматериалов) за этим стоит. count &gt; distinctCount значит в
 * списке есть повторы — см. dedupeFailedJobs. */
export interface AttentionReason {
  reason: EnrichmentFailureReasonValue | 'Unclassified'; label: string; count: number; distinctCount: number;
  byType: Record<string, number>;
}
export interface DroppedDomain { domain: string; topic: string; jobCount: number; sampleUrl: string; }

/** Закрытый вентиль платного поиска (ADR-0005 §9) откладывает задачи молча (Deferred) — без
 * этого блока «Требует внимания» показал бы закрытый вентиль как зависший конвейер: ни одной
 * ошибки, просто ничего не движется. */
export interface WebSearchPausedBlock {
  isPaused: boolean; pausedAt: string | null; note: string | null; deferredTotal: number; byType: Record<string, number>;
}
export interface AdminAttention {
  reasons: AttentionReason[]; droppedDomains: DroppedDomain[]; webSearchPaused: WebSearchPausedBlock;
}
export interface TrustAndRetryResponse { retriedCount: number; }

/** activeModel=null означает, что в БД ничего не выбрано и клиент шлёт fallbackModel
 * (LmStudioOptions.Model, appsettings/env) — см. ILmStudioModelProvider на бэкенде. */
export interface LmStudioModelInfo { activeModel: string | null; fallbackModel: string; }
export interface LmStudioAvailableModels { models: string[]; lmStudioReachable: boolean; }

/** См. FamilyHub.Domain.Enums.LmStudioReasoning — зеркало NotificationType и т.п. (число, не строка). */
export const LmStudioReasoning = {
  None: 0,
  Minimal: 1,
  Medium: 2,
  Maximum: 3,
} as const;
export type LmStudioReasoning = typeof LmStudioReasoning[keyof typeof LmStudioReasoning];

/** activeReasoning=null означает, что в БД ничего не выбрано и клиент шлёт fallbackReasoning
 * (LmStudioOptions.Reasoning, appsettings/env) — тот же приём, что LmStudioModelInfo выше. */
export interface LmStudioReasoningInfo {
  activeReasoning: LmStudioReasoning | null;
  fallbackReasoning: LmStudioReasoning;
}

/**
 * Клиент /api/admin/*. Отдельно от ApiService (api.service.ts) намеренно — другая поверхность
 * аутентификации (cookie familyhub.admin, схема AuthSchemes.Admin, см. ADR-0009), не должна
 * смешиваться с обычной PWA/Telegram-сессией пользователя.
 */
@Injectable({ providedIn: 'root' })
export class AdminApiService {
  private readonly http = inject(HttpClient);
  private readonly log = inject(DevLoggerService);

  private async get<T>(path: string): Promise<T> {
    this.log.log('api', 'info', `GET ${path}`);
    try {
      return await firstValueFrom(this.http.get<T>(path));
    } catch (e) {
      throw this.toApiError(e);
    }
  }

  private async post<T>(path: string, body: unknown = null): Promise<T> {
    this.log.log('api', 'info', `POST ${path}`);
    try {
      return await firstValueFrom(this.http.post<T>(path, body));
    } catch (e) {
      throw this.toApiError(e);
    }
  }

  private async put<T>(path: string, body: unknown = null): Promise<T> {
    this.log.log('api', 'info', `PUT ${path}`);
    try {
      return await firstValueFrom(this.http.put<T>(path, body));
    } catch (e) {
      throw this.toApiError(e);
    }
  }

  private async del<T>(path: string): Promise<T> {
    try {
      return await firstValueFrom(this.http.delete<T>(path));
    } catch (e) {
      throw this.toApiError(e);
    }
  }

  private toApiError(e: unknown): ApiError {
    if (e instanceof HttpErrorResponse) {
      const msg = typeof e.error === 'string' ? e.error : (e.error?.code ?? e.statusText);
      return new ApiError(e.status, msg);
    }
    return new ApiError(0, 'Неизвестная ошибка');
  }

  login = (user: string, password: string) => this.post<void>('/api/admin/session', { user, password });
  logout = () => this.del<void>('/api/admin/session');
  checkSession = () => this.get<void>('/api/admin/session');

  getOverview = () => this.get<AdminOverview>('/api/admin/stats/overview');
  getStorageStats = (recalculate = false) =>
    this.get<AdminStorageStats>(`/api/admin/stats/storage${recalculate ? '?recalculate=true' : ''}`);
  getSystemStats = () => this.get<AdminSystemStats>('/api/admin/stats/system');
  getSecurityStats = () => this.get<AdminSecurityStats>('/api/admin/stats/security');

  getKeyRings = () => this.get<AdminKeyRings>('/api/admin/keys');

  /** Эффективная конфигурация (read-only): несекретные настройки по белому списку + «задан / не задан»
   * для секретов. */
  getConfig = () => this.get<AdminConfig>('/api/admin/config');

  // Ротация учёток приложения (ADR-0011). Ошибки бизнес-правил приходят как ApiError.message = code
  // (not_least_privilege | not_service_account | old_not_revoked | in_use | nothing_to_revoke |
  // pending_rotation | storage_unavailable | storage_rejected).
  getCredentials = () => this.get<CredentialsStatus>('/api/admin/credentials');
  generateCredentials = (kind: CredentialKind) =>
    this.post<GeneratedCredential>(`/api/admin/credentials/${kind === 'Postgres' ? 'postgres' : 'minio'}/generate`);
  revokeOldCredentials = (kind: CredentialKind) =>
    this.post<RevokedCredential>(`/api/admin/credentials/${kind === 'Postgres' ? 'postgres' : 'minio'}/revoke-old`);
  startRotation = () => this.post<void>('/api/admin/keys/encryption/rotate');
  cancelRotation = () => this.post<void>('/api/admin/keys/encryption/rotate/cancel');
  getRotationStatus = () => this.get<RotationStatus>('/api/admin/keys/encryption/rotate/status');

  // Пересборка enrich-пайплайна — доверенные домены (БД-backed) + кэш сырых результатов поиска
  // (хранит ВСЕ сниппеты, не только доверенные) обоих конвейеров обогащения.
  getTrustedDomains = (topic: WebSearchTopicValue) =>
    this.get<TrustedDomain[]>(`/api/admin/enrichment/trusted-domains?topic=${topic}`);

  addTrustedDomain = (topic: WebSearchTopicValue, domain: string) =>
    this.post<TrustedDomain>('/api/admin/enrichment/trusted-domains', { topic, domain });

  setTrustedDomainEnabled = (id: string, isEnabled: boolean) =>
    this.put<void>(`/api/admin/enrichment/trusted-domains/${id}`, { isEnabled });

  deleteTrustedDomain = (id: string) => this.del<void>(`/api/admin/enrichment/trusted-domains/${id}`);

  reorderTrustedDomains = (topic: WebSearchTopicValue, orderedIds: string[]) =>
    this.post<void>('/api/admin/enrichment/trusted-domains/reorder', { topic, orderedIds });

  getSearchCache = (topic: WebSearchTopicValue, query: string, skip: number, take: number) =>
    this.get<SearchCacheListResponse>(
      `/api/admin/enrichment/search-cache?topic=${topic}&query=${encodeURIComponent(query)}&skip=${skip}&take=${take}`);

  getSearchCacheDetail = (id: string, topic: WebSearchTopicValue) =>
    this.get<SearchCacheDetail>(`/api/admin/enrichment/search-cache/${id}?topic=${topic}`);

  setSnippetOverride = (id: string, topic: WebSearchTopicValue, url: string, enabled: boolean | null) =>
    this.post<void>(`/api/admin/enrichment/search-cache/${id}/override`, { topic, url, enabled });

  /** Полное редактирование строки кэша — snippets заменяет весь список (добавить/отредактировать/
   * убрать сниппет — одно и то же действие «сохранить новый список»). */
  updateSearchCache = (id: string, request: UpdateSearchCacheRequest) =>
    this.put<void>(`/api/admin/enrichment/search-cache/${id}`, request);

  deleteSearchCache = (id: string, topic: WebSearchTopicValue) =>
    this.del<void>(`/api/admin/enrichment/search-cache/${id}?topic=${topic}`);

  /** Массовая очистка кэша показателей с нерезолвленным источником — наследие до пересборки
   * enrich-пайплайна анализов (жёсткий гейт больше не даёт таким строкам появляться заново). */
  purgeUnresolvedSpecimenSearchCache = () =>
    this.post<{ deletedCount: number }>('/api/admin/enrichment/search-cache/lab-analytes/purge-unresolved-specimen');

  // Аудит платных вызовов внешнего веб-поиска (см. AdminSearchCallsEndpoints) — «сколько заплачено
  // и за что», полный текст запроса каждого вызова, для дебага работы кэша/квоты.
  getSearchCalls = (
    filters: {
      provider?: string; topic?: WebSearchTopicValue; outcome?: WebSearchCallOutcomeValue;
      query?: string; from?: string; to?: string;
    },
    page: number, pageSize: number,
  ) => {
    const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
    if (filters.provider) params.set('provider', filters.provider);
    if (filters.topic !== undefined) params.set('topic', String(filters.topic));
    if (filters.outcome !== undefined) params.set('outcome', String(filters.outcome));
    if (filters.query) params.set('query', filters.query);
    if (filters.from) params.set('from', filters.from);
    if (filters.to) params.set('to', filters.to);
    return this.get<SearchCallListResponse>(`/api/admin/search-calls?${params.toString()}`);
  };

  getSearchCallDetail = (id: string) => this.get<SearchCallDetail>(`/api/admin/search-calls/${id}`);

  getSearchCallStats = (from?: string, to?: string) => {
    const params = new URLSearchParams();
    if (from) params.set('from', from);
    if (to) params.set('to', to);
    const qs = params.toString();
    return this.get<SearchCallStats>(`/api/admin/search-calls/stats${qs ? `?${qs}` : ''}`);
  };

  // Полная пересборка справочника показателей (§4.2 плана) — разовое ручное действие после
  // деплоя исправлений очистки имён/резолвинга источника, отдельно от reenrich (который реагирует
  // на дрейф PayloadVersion построчно и запускается автоматически).
  startKbRebuild = () => this.post<void>('/api/admin/kb/lab-analytes/rebuild');

  /** Принудительное переобогащение показателей со старой схемой payload батчами
   * (LabAnalyteKbReenrichJob) — фоновая задача, эндпоинт сразу отвечает 202. Не путать с
   * reenrichLabAnalyte ниже (один показатель по id) и с полной пересборкой выше. */
  reenrichLabAnalytesBatch = () => this.post<void>('/api/admin/kb/lab-analytes/reenrich');
  getKbRebuildStatus = () => this.get<KbRebuildStatus>('/api/admin/kb/lab-analytes/rebuild/status');

  // Прогрев кэша веб-поиска из админки (грантовый лимит облака) — см. AdminWarmupEndpoints.
  // Ошибки 400/409 приходят с ApiError.message = code ("specimen_required" | "nothing_to_do" |
  // "already_running") — см. toApiError выше.
  startWarmup = (request: StartWarmupRequest) => this.post<WarmupStatus>('/api/admin/enrichment/warmup', request);
  cancelWarmup = () => this.post<void>('/api/admin/enrichment/warmup/cancel');
  getWarmupStatus = () => this.get<WarmupStatus>('/api/admin/enrichment/warmup/status');

  getWebSearchValve = () => this.get<WebSearchValve>('/api/admin/enrichment/web-search');
  setWebSearchValve = (isPaused: boolean, note: string | null) =>
    this.put<void>('/api/admin/enrichment/web-search', { isPaused, note });

  // Управление enrich-пайплайном из админки (§2 плана) — вкл/выкл необязательных шагов,
  // версионирование промптов, dry-run без записи, листинг задач всех четырёх конвейеров.
  getPipelineSteps = () => this.get<PipelineStep[]>('/api/admin/pipeline/pipelines');

  setStepEnabled = (pipelineKey: string, stepKey: string, isEnabled: boolean) =>
    this.put<void>(`/api/admin/pipeline/pipelines/${pipelineKey}/steps/${stepKey}`, { isEnabled });

  getPromptSlots = () => this.get<PromptSlot[]>('/api/admin/pipeline/prompts');

  getPromptVersions = (key: string) => this.get<PromptVersion[]>(`/api/admin/pipeline/prompts/${key}/versions`);

  createPromptVersion = (key: string, body: string, note: string | null) =>
    this.post<PromptVersion>(`/api/admin/pipeline/prompts/${key}/versions`, { body, note });

  activatePromptVersion = (key: string, version: number) =>
    this.post<void>(`/api/admin/pipeline/prompts/${key}/activate/${version}`);

  dryRunPrompt = (promptKey: string, bodyOverride: string | null, userText: string) =>
    this.post<DryRunResponse>('/api/admin/pipeline/prompts/dry-run', { promptKey, bodyOverride, userText });

  // Выбор активной модели LM Studio из админки — тот же приём, что промпты выше.
  getLmStudioModel = () => this.get<LmStudioModelInfo>('/api/admin/lmstudio/model');

  getAvailableLmStudioModels = () => this.get<LmStudioAvailableModels>('/api/admin/lmstudio/available-models');

  setLmStudioModel = (modelId: string | null) => this.put<void>('/api/admin/lmstudio/model', { modelId });

  // Уровень "размышлений" LM Studio из админки — тот же приём, что модель выше, позволяет
  // сравнивать скорость/глубину рассуждений на лету, без передеплоя.
  getLmStudioReasoning = () => this.get<LmStudioReasoningInfo>('/api/admin/lmstudio/reasoning');

  setLmStudioReasoning = (reasoning: LmStudioReasoning | null) =>
    this.put<void>('/api/admin/lmstudio/reasoning', { reasoning });

  getPipelineJobs = (type: PipelineJobType, status: string | null, skip: number, take: number, reason: string | null = null) =>
    this.get<PipelineJobListResponse>(
      `/api/admin/pipeline/jobs?type=${type}${status ? `&status=${status}` : ''}${reason ? `&reason=${reason}` : ''}&skip=${skip}&take=${take}`);

  getPipelineJobDetail = (id: string, type: PipelineJobType) =>
    this.get<PipelineJobDetail>(`/api/admin/pipeline/jobs/${id}?type=${type}`);

  retryPipelineJob = (id: string, type: PipelineJobType) =>
    this.post<void>(`/api/admin/pipeline/jobs/${id}/retry?type=${type}`);

  /** «Применить и перезапустить» из карточки задачи — доверяет домены/override'ы конкретных URL
   * и перезапускает одним запросом, без перехода на вкладку «Обогащение» и обратно. */
  resolveAndRetryJob = (id: string, type: PipelineJobType, request: ResolveAndRetryRequest) =>
    this.post<void>(`/api/admin/pipeline/jobs/${id}/resolve-and-retry?type=${type}`, request);

  bulkRetryJobs = (type: PipelineJobType, ids: string[]) =>
    this.post<BulkRetryResponse>('/api/admin/pipeline/jobs/bulk-retry', { type, ids });

  deleteJob = (id: string, type: PipelineJobType) => this.del<void>(`/api/admin/pipeline/jobs/${id}?type=${type}`);

  bulkDeleteJobs = (type: PipelineJobType, ids: string[]) =>
    this.post<BulkDeleteResponse>('/api/admin/pipeline/jobs/bulk-delete', { type, ids });

  /** Чистка задач, упавших до появления структурной причины отказа (FailureReason=null,
   * «Unclassified» в «Требует внимания») — их незачем разбирать по одной, причины у них нет. */
  purgeUnclassifiedJobs = () => this.post<PurgeUnclassifiedResponse>('/api/admin/pipeline/jobs/purge-unclassified');

  /** Чистка УЖЕ накопленных дублей Failed/Skipped-строк за одно и то же название — новые дубли
   * больше не создаются (см. class doc *RequestService на бэкенде), но это не чистит задним
   * числом то, что уже есть. В каждой группе (NormalizedName[, биоматериал]) остаётся только
   * самая свежая строка. */
  dedupeFailedJobs = () => this.post<DedupeFailedResponse>('/api/admin/pipeline/jobs/dedupe-failed');

  reenrichLabAnalyte = (id: string) => this.post<void>(`/api/admin/pipeline/kb/lab-analytes/${id}/reenrich`);

  /** Одноразовый перепрогон показателей, застрявших на Flag.Unknown ДО фикса каскада
   * IndicatorFlagCalculator (односторонние референсы "<47"/">47", качественные результаты вида
   * "не обнаружено") — RecomputeIndicatorFlagsBackfillJob, ставится в Hangfire-очередь и работает
   * в фоне, эндпоинт сразу отвечает 202. */
  recomputeIndicatorFlags = () => this.post<void>('/api/admin/pipeline/recompute-indicator-flags');

  // Инбокс «Требует внимания» — точка входа админки в разбор падений конвейера (см. план, Context).
  getAttention = () => this.get<AdminAttention>('/api/admin/pipeline/attention');

  trustDomainsAndRetry = (topic: WebSearchTopicValue, domains: string[]) =>
    this.post<TrustAndRetryResponse>('/api/admin/pipeline/attention/trust-and-retry', { topic, domains });

  // Ручная правка справочников после ИИ (§3 плана) — показатели, медикаменты, источники.
  searchLabAnalytes = (q: string, skip: number, take: number) =>
    this.get<KbAnalyteListResponse>(`/api/admin/kb/lab-analytes?q=${encodeURIComponent(q)}&skip=${skip}&take=${take}`);

  getLabAnalyte = (id: string) => this.get<AdminLabAnalyteDetail>(`/api/admin/kb/lab-analytes/${id}`);

  updateLabAnalyte = (id: string, request: AdminKbEditRequest) =>
    this.put<AdminLabAnalyteDetail>(`/api/admin/kb/lab-analytes/${id}`, request);

  unlockLabAnalyteField = (id: string, field: string) => this.del<void>(`/api/admin/kb/lab-analytes/${id}/locks/${field}`);

  /** Пикер «Что смотрят вместе» — резолвит имена в реальные строки справочника (id/специмен для
   * disambiguation/кликабельной ссылки), точным совпадением NormalizedName. */
  resolveRelatedAnalytes = (names: string[]) =>
    this.post<AdminRelatedAnalyteMatch[]>('/api/admin/kb/lab-analytes/resolve-related', names);

  deleteLabAnalyte = (id: string) => this.del<void>(`/api/admin/kb/lab-analytes/${id}`);

  mergeLabAnalytes = (loserId: string, winnerId: string) =>
    this.post<void>(`/api/admin/kb/lab-analytes/${loserId}/merge-into/${winnerId}`);

  searchMedications = (q: string, skip: number, take: number) =>
    this.get<KbListResponse>(`/api/admin/kb/medications?q=${encodeURIComponent(q)}&skip=${skip}&take=${take}`);

  getMedication = (id: string) => this.get<AdminMedicationDetail>(`/api/admin/kb/medications/${id}`);

  updateMedication = (id: string, request: AdminKbEditRequest) =>
    this.put<AdminMedicationDetail>(`/api/admin/kb/medications/${id}`, request);

  unlockMedicationField = (id: string, field: string) => this.del<void>(`/api/admin/kb/medications/${id}/locks/${field}`);

  deleteMedication = (id: string) => this.del<void>(`/api/admin/kb/medications/${id}`);

  searchSpecimens = (q: string, take = 20) =>
    this.get<GlobalSpecimen[]>(`/api/admin/kb/specimens?q=${encodeURIComponent(q)}&take=${take}`);

  renameSpecimen = (id: string, displayName: string) =>
    this.put<void>(`/api/admin/kb/specimens/${id}`, { displayName });

  deleteSpecimen = (id: string) => this.del<void>(`/api/admin/kb/specimens/${id}`);

  mergeSpecimens = (loserId: string, winnerId: string) =>
    this.post<void>(`/api/admin/kb/specimens/${loserId}/merge-into/${winnerId}`);
}
