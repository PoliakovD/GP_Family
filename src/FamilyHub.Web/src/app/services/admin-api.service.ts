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

/** Прогон пересборки справочника показателей (пересборка enrich-пайплайна, §4.2 плана) — зеркало
 * RotationStatus на LabAnalyteKbRebuildJob. status: "Running" | "Completed" | "Failed" | null. */
export interface KbRebuildStatus {
  runId: string | null; status: string | null; startedAt: string | null; finishedAt: string | null;
  lastError: string | null; stageIndex: number;
  cacheMerged: number; indicatorsUpdated: number; indicatorsMerged: number;
  catalogDeleted: number; reseedRequested: number;
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

/** Один пункт инбокса «Требует внимания» — агрегат причин отказа по всем четырём конвейерам. */
export interface AttentionReason {
  reason: EnrichmentFailureReasonValue | 'Unclassified'; label: string; count: number;
  byType: Record<string, number>;
}
export interface DroppedDomain { domain: string; topic: string; jobCount: number; sampleUrl: string; }
export interface AdminAttention { reasons: AttentionReason[]; droppedDomains: DroppedDomain[]; }
export interface TrustAndRetryResponse { retriedCount: number; }

/** activeModel=null означает, что в БД ничего не выбрано и клиент шлёт fallbackModel
 * (LmStudioOptions.Model, appsettings/env) — см. ILmStudioModelProvider на бэкенде. */
export interface LmStudioModelInfo { activeModel: string | null; fallbackModel: string; }
export interface LmStudioAvailableModels { models: string[]; lmStudioReachable: boolean; }

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

  // Полная пересборка справочника показателей (§4.2 плана) — разовое ручное действие после
  // деплоя исправлений очистки имён/резолвинга источника, отдельно от reenrich (который реагирует
  // на дрейф PayloadVersion построчно и запускается автоматически).
  startKbRebuild = () => this.post<void>('/api/admin/kb/lab-analytes/rebuild');
  getKbRebuildStatus = () => this.get<KbRebuildStatus>('/api/admin/kb/lab-analytes/rebuild/status');

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

  reenrichLabAnalyte = (id: string) => this.post<void>(`/api/admin/pipeline/kb/lab-analytes/${id}/reenrich`);

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
