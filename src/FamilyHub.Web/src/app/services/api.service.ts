import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpEventType } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import {
  AppNotification,
  Attachment,
  Birthday,
  BirthdayInput, CurrentMember,
  EnrichmentRefreshOutcome,
  FamilySummary,
  InviteCreated,
  KbListResponse,
  KbMedicationCard,
  MedicalRecord,
  MedicalRecordInput,
  Medication,
  MedicationInput,
  MedicationKbResponse,
  MedicationOcrResponse,
  Medkit,
  MedkitInput,
  NotificationPreference,
  PendingMember, RemoveMemberResult,
  SearchResponse,
  VapidPublicKeyResponse,
} from '../models/types';
import { FamilyRole } from '../models/types';
import { DevLoggerService } from './dev-logger.service';

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly log = inject(DevLoggerService);


  private async get<T>(path: string): Promise<T> {
    this.log.log('api', 'info', `GET ${path}`);
    try {
      const result = await firstValueFrom(this.http.get<T>(path));
      this.log.log('api', 'info', `GET ${path} ✓`);
      return result;
    } catch (e) {
      const err = this.toApiError(e);
      this.log.log('api', 'error', `GET ${path} ✗ ${err.status}: ${err.message}`);
      throw err;
    }
  }

  private async post<T>(path: string, body: unknown = null): Promise<T> {
    this.log.log('api', 'info', `POST ${path}`);
    try {
      const result = await firstValueFrom(this.http.post<T>(path, body));
      this.log.log('api', 'info', `POST ${path} ✓`);
      return result as T;
    } catch (e) {
      const err = this.toApiError(e);
      this.log.log('api', 'error', `POST ${path} ✗ ${err.status}: ${err.message}`);
      throw err;
    }
  }

  private async put<T>(path: string, body: unknown = null): Promise<T> {
    this.log.log('api', 'info', `PUT ${path}`);
    try {
      const result = await firstValueFrom(this.http.put<T>(path, body));
      this.log.log('api', 'info', `PUT ${path} ✓`);
      return result as T;
    } catch (e) {
      const err = this.toApiError(e);
      this.log.log('api', 'error', `PUT ${path} ✗ ${err.status}: ${err.message}`);
      throw err;
    }
  }

  private async del<T>(path: string): Promise<T> {
    this.log.log('api', 'info', `DELETE ${path}`);
    try {
      const result = await firstValueFrom(this.http.delete<T>(path));
      this.log.log('api', 'info', `DELETE ${path} ✓`);
      return result as T;
    } catch (e) {
      const err = this.toApiError(e);
      this.log.log('api', 'error', `DELETE ${path} ✗ ${err.status}: ${err.message}`);
      throw err;
    }
  }

  private toApiError(e: unknown): ApiError {
    if (e instanceof HttpErrorResponse) {
      const msg = typeof e.error === 'string' ? e.error : e.statusText;
      return new ApiError(e.status, msg);
    }
    return new ApiError(0, 'Неизвестная ошибка');
  }

  // Семьи
  getFamilies = () => this.get<FamilySummary[]>('/api/families');

  createFamily = (name: string) => this.post<{ id: string }>('/api/families', { name });

  deleteFamily = (familyId: string) => this.del<void>(`/api/families/${familyId}`);

  getPendingMembers = (familyId: string) => this.get<PendingMember[]>(`/api/families/${familyId}/pending`);

  getCurrentMembers = (familyId: string) => this.get<CurrentMember[]>(`/api/families/${familyId}/current`);

  approveMember = (familyId: string, userId: string) =>
    this.post<void>(`/api/families/${familyId}/members/${userId}/approve`);

  rejectMember = (familyId: string, userId: string) =>
    this.post<void>(`/api/families/${familyId}/members/${userId}/reject`);

  async removeMember(familyId: string, userId: string): Promise<RemoveMemberResult> {
    try {
      // Если бэкенд вернул Results.NoContent(), это статус 204. post() вернет null/undefined.
      await this.post<void>(`/api/families/${familyId}/members/${userId}/remove`);
      return RemoveMemberResult.Removed; // 0
    } catch (e) {
      // Ловим нашу кастомную ApiError
      if (e instanceof ApiError) {
        switch (e.status) {
          case 403: return RemoveMemberResult.Forbidden; // 1
          case 404: return RemoveMemberResult.NotFound;  // 2
          case 409: return RemoveMemberResult.LastAdmin; // 3
        }
      }
      // Если произошла сетевая ошибка или 500, пробрасываем её дальше
      throw e;
    }
  }

  createInvite = (familyId: string) =>
    this.post<InviteCreated>(`/api/families/${familyId}/invites`, {
      targetUserId: null,
      assignedRole: FamilyRole.Member,
      maxUses: 1,
      expiresAt: null,
    });

  redeemInvite = (code: string) => this.post<{ status: string }>(`/api/invites/${code}/redeem`);

  // Аптечки
  getMedkits = (familyId: string) => this.get<Medkit[]>(`/api/families/${familyId}/medkits`);

  createMedkit = (familyId: string, input: MedkitInput) =>
    this.post<Medkit>(`/api/families/${familyId}/medkits`, input);

  updateMedkit = (id: string, input: MedkitInput) => this.put<void>(`/api/medkits/${id}`, input);

  deleteMedkit = (id: string) => this.del<void>(`/api/medkits/${id}`);

  // Медикаменты внутри аптечки
  getMedications = (medkitId: string) => this.get<Medication[]>(`/api/medkits/${medkitId}/medications`);

  createMedication = (medkitId: string, input: MedicationInput) =>
    this.post<Medication>(`/api/medkits/${medkitId}/medications`, input);

  updateMedication = (id: string, input: MedicationInput) =>
    this.put<void>(`/api/medications/${id}`, input);

  deleteMedication = (id: string) => this.del<void>(`/api/medications/${id}`);

  /**
   * onUploadProgress получает реальный процент отправки файлов (через HttpEventType.UploadProgress) —
   * используется UI, чтобы отличить "отправляем" от "ждём ответа модели" вместо статичного спиннера.
   */
  ocrMedicationPhotos(files: Blob[], onUploadProgress?: (percent: number) => void): Promise<MedicationOcrResponse> {
    this.log.log('api', 'info', `POST /api/medications/ocr (${files.length} фото)`);
    const formData = new FormData();
    files.forEach((file, i) => formData.append('files', file, `photo-${i}.jpg`));

    return new Promise<MedicationOcrResponse>((resolve, reject) => {
      this.http
        .post<MedicationOcrResponse>('/api/medications/ocr', formData, {
          reportProgress: true,
          observe: 'events',
        })
        .subscribe({
          next: (event) => {
            if (event.type === HttpEventType.UploadProgress && event.total) {
              onUploadProgress?.(Math.round((100 * event.loaded) / event.total));
            } else if (event.type === HttpEventType.Response && event.body) {
              this.log.log('api', 'info', `POST /api/medications/ocr ✓ success=${event.body.success}`);
              resolve(event.body);
            }
          },
          error: (e) => {
            const err = this.toApiError(e);
            this.log.log('api', 'error', `POST /api/medications/ocr ✗ ${err.status}: ${err.message}`);
            reject(err);
          },
        });
    });
  }

  // Дни рождения
  getBirthdays = (familyId: string) => this.get<Birthday[]>(`/api/families/${familyId}/birthdays`);

  createBirthday = (familyId: string, input: BirthdayInput) =>
    this.post<Birthday>(`/api/families/${familyId}/birthdays`, input);

  updateBirthday = (id: string, input: BirthdayInput) =>
    this.put<void>(`/api/birthdays/${id}`, input);

  deleteBirthday = (id: string) => this.del<void>(`/api/birthdays/${id}`);

  // Анализы и посещения врачей — единая таблица на бэкенде (MedicalRecordKind), kind опционален
  // ("analysis"/"visit") — без него отдаются оба вида.
  getMedicalRecords = (kind?: 'analysis' | 'visit') =>
    this.get<MedicalRecord[]>(`/api/medical-records${kind ? `?kind=${kind}` : ''}`);

  /** Список вложений записи — грузится с сервера (не копится в памяти сессии, как раньше). */
  getRecordAttachments = (recordId: string) => this.get<Attachment[]>(`/api/medical-records/${recordId}/attachments`);

  /** L1-семьи (расшарены глобально владельцем) — состояние для тумблеров в bottom-sheet «Доступ». */
  getMedicalRecordShares = () => this.get<string[]>('/api/medical-records/shares');

  createMedicalRecord = (input: MedicalRecordInput) =>
    this.post<MedicalRecord>('/api/medical-records', input);

  shareMedicalRecord = (familyId: string) =>
    this.post<void>('/api/medical-records/share', { familyId });

  unshareMedicalRecord = (familyId: string) =>
    this.post<void>('/api/medical-records/unshare', { familyId });

  hideMedicalRecord = (recordId: string, familyIds: string[]) =>
    this.post<void>(`/api/medical-records/${recordId}/hide`, { familyIds });

  unhideMedicalRecord = (recordId: string, familyIds: string[]) =>
    this.post<void>(`/api/medical-records/${recordId}/unhide`, { familyIds });

  getAttachmentUrl = (id: string) => this.get<{ url: string }>(`/api/attachments/${id}/url`);

  async uploadAttachment(recordId: string, file: File): Promise<Attachment> {
    this.log.log('api', 'info', `POST /api/medical-records/${recordId}/attachments (${file.name})`);
    const formData = new FormData();
    formData.append('file', file);
    try {
      const result = await firstValueFrom(
        this.http.post<Attachment>(`/api/medical-records/${recordId}/attachments`, formData),
      );
      this.log.log('api', 'info', `POST attachments ✓ id=${result.id}`);
      return result;
    } catch (e) {
      const err = this.toApiError(e);
      this.log.log('api', 'error', `POST attachments ✗ ${err.status}: ${err.message}`);
      throw err;
    }
  }

  // Оповещения
  getNotifications = (unreadOnly: boolean) =>
    this.get<AppNotification[]>(`/api/notifications?unreadOnly=${unreadOnly}`);
  markNotificationRead = (id: string) => this.post<void>(`/api/notifications/${id}/read`);

  // Предпочтения доставки по типу оповещения (вкладка «Настройки → Уведомления»).
  getNotificationPreferences = () => this.get<NotificationPreference[]>('/api/notifications/preferences');
  saveNotificationPreferences = (prefs: NotificationPreference[]) =>
    this.put<void>('/api/notifications/preferences', prefs);

  // Поиск (этап 3): гибрид Postgres-FTS (лекарства, справочник) + in-memory (анализы) — см. SearchService.
  // types — опциональный серверный фильтр источников ("medication"/"kb"/"record", можно через
  // запятую); не запрошенный источник бэкенд вообще не трогает (см. SearchService.SearchAsync).
  search = (q: string, types?: string) => {
    const typesQuery = types ? `&types=${encodeURIComponent(types)}` : '';
    return this.get<SearchResponse>(`/api/search?q=${encodeURIComponent(q)}${typesQuery}`);
  };

  // Справочник препаратов (этап 4) — общий обезличенный, наполняется AI-конвейером обогащения.
  searchKb = (q?: string, skip = 0, take = 20) => {
    const qQuery = q ? `&q=${encodeURIComponent(q)}` : '';
    return this.get<KbListResponse>(`/api/kb/medications?skip=${skip}&take=${take}${qQuery}`);
  };

  getKbMedication = (id: string) => this.get<KbMedicationCard>(`/api/kb/medications/${id}`);

  /** Статус обогащения конкретного медикамента пользователя + карточка, если уже готова. */
  getMedicationKb = (medicationId: string) => this.get<MedicationKbResponse>(`/api/medications/${medicationId}/kb`);

  /** Ручной запрос «Уточнить в справочнике» — конвейер асинхронный (см. getMedicationKb для статуса
   * после Requested); при OnCooldown задача не ставится — платный API по этому названию уже
   * запрашивался недавно (см. EnrichmentRefreshOutcome.availableAt). */
  refreshMedicationKb = (medicationId: string) =>
    this.post<EnrichmentRefreshOutcome>(`/api/medications/${medicationId}/kb/refresh`);

  // Web Push (редизайн навигации, ADR-0004)
  getPushVapidPublicKey = () => this.get<VapidPublicKeyResponse>('/api/push/vapid-public-key');

  subscribePush = (endpoint: string, p256dh: string, auth: string) =>
    this.post<void>('/api/push/subscribe', { endpoint, p256dh, auth });

  unsubscribePush = (endpoint: string) => this.post<void>('/api/push/unsubscribe', { endpoint });
}
