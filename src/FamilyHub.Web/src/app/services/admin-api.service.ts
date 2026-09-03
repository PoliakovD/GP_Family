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

export interface DryRunResponse { success: boolean; error: string | null; payload: Record<string, unknown> | null; }

export type PipelineJobType = 'lab-analyte' | 'medication' | 'visit-medication' | 'extraction';

export interface PipelineJob {
  id: string; type: PipelineJobType; displayName: string; status: string; attempts: number;
  error: string | null; createdAt: string; startedAt: string | null; completedAt: string | null;
}
export interface PipelineJobListResponse { rows: PipelineJob[]; total: number; }

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

  getPipelineJobs = (type: PipelineJobType, status: string | null, skip: number, take: number) =>
    this.get<PipelineJobListResponse>(
      `/api/admin/pipeline/jobs?type=${type}${status ? `&status=${status}` : ''}&skip=${skip}&take=${take}`);

  retryPipelineJob = (id: string, type: PipelineJobType) =>
    this.post<void>(`/api/admin/pipeline/jobs/${id}/retry?type=${type}`);

  reenrichLabAnalyte = (id: string) => this.post<void>(`/api/admin/pipeline/kb/lab-analytes/${id}/reenrich`);
}
