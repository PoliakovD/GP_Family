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
}
