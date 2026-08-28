import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpEventType } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import {
  AppNotification,
  Attachment,
  AttachmentLimits,
  Birthday,
  BirthdayInput, CurrentMember,
  CreateIndicatorRequest,
  EnrichmentRefreshOutcome,
  ExtractionStatusResponse,
  FamilyDependent,
  FamilyDependentInput,
  FamilySummary,
  HomeSummaryResponse,
  IndicatorArticleResponse,
  IndicatorDto,
  IndicatorHistoryPoint,
  InviteCreated,
  KbAnalyteCard,
  KbAnalyteListResponse,
  KbListResponse,
  KbMedicationCard,
  MedicalRecord,
  MedicalRecordFilter,
  MedicalRecordInput,
  UpdateMedicalRecordRequest,
  Medication,
  MedicationInput,
  MedicationKbResponse,
  MedicationOcrResponse,
  Medkit,
  MedkitInput,
  MyIndicatorSummary,
  NotificationPreference,
  PagedResult,
  PendingMember, RecordSummaryResponse, RemoveMemberResult,
  SearchResponse,
  UpdateIndicatorRequest,
  UserSpecimen,
  VapidPublicKeyResponse,
  VisitConclusion,
} from '../models/types';
import { FamilyRole } from '../models/types';
import { DevLoggerService } from './dev-logger.service';

/** Сериализует плоский объект фильтров в "?a=1&b=2" — undefined/null/'' поля пропускаются
 * (серверные MedicalRecordFilter/GetHistoryAsync все параметры опциональны). booleans/numbers
 * приводятся к строке как есть. */
function buildQuery(params: object): string {
  const parts = Object.entries(params as Record<string, string | number | boolean | undefined | null>)
    .filter(([, v]) => v !== undefined && v !== null && v !== '')
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`);
  return parts.length > 0 ? `?${parts.join('&')}` : '';
}

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
      // JSON-тело ошибки ({code, reason} — напр. UserSpecimenEndpoints) несёт человекочитаемую
      // причину от LLM-гейта; раньше она терялась (msg падал на generic e.statusText, т.к.
      // e.error — объект, не строка).
      const body: unknown = e.error;
      const reason = typeof body === 'object' && body !== null
        ? ((body as { reason?: string; message?: string }).reason ?? (body as { message?: string }).message)
        : undefined;
      const msg = typeof body === 'string' ? body : (reason ?? e.statusText);
      return new ApiError(e.status, msg);
    }
    return new ApiError(0, 'Неизвестная ошибка');
  }

  // Редизайн v2 — агрегат Главной, одним запросом вместо 3-4 отдельных.
  getHomeSummary = () => this.get<HomeSummaryResponse>('/api/home/summary');

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

  // Подопечные (дети/питомцы/пожилые родственники без своего User) — семейный ресурс, тот же
  // паттерн, что аптечки/дни рождения. Create/Update — любой активный участник, Delete — только Admin.
  getDependents = (familyId: string) => this.get<FamilyDependent[]>(`/api/families/${familyId}/dependents`);

  createDependent = (familyId: string, input: FamilyDependentInput) =>
    this.post<FamilyDependent>(`/api/families/${familyId}/dependents`, input);

  updateDependent = (id: string, input: FamilyDependentInput) =>
    this.put<void>(`/api/dependents/${id}`, input);

  deleteDependent = (id: string) => this.del<void>(`/api/dependents/${id}`);

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
  // ("analysis"/"visit") — без него отдаются оба вида. UX-редизайн: серверные фильтры +
  // пагинация (дефолт 15/стр.) вместо голого списка.
  getMedicalRecords = (filter: MedicalRecordFilter = {}) =>
    this.get<PagedResult<MedicalRecord>>(`/api/medical-records${buildQuery(filter)}`);

  /** Список вложений записи — грузится с сервера (не копится в памяти сессии, как раньше). */
  getRecordAttachments = (recordId: string) => this.get<Attachment[]>(`/api/medical-records/${recordId}/attachments`);

  /** L1-семьи (расшарены глобально владельцем) — состояние для тумблеров в bottom-sheet «Доступ». */
  getMedicalRecordShares = () => this.get<string[]>('/api/medical-records/shares');

  /** Автоподсказка «Врач» (v2) — доктора, которых пользователь уже вводил в СВОИХ записях. */
  getDoctorSuggestions = () => this.get<string[]>('/api/medical-records/doctors');

  createMedicalRecord = (input: MedicalRecordInput) =>
    this.post<MedicalRecord>('/api/medical-records', input);

  /** Правка даты/врача/описания (UX-редизайн) — только владелец, пациент/вид записи неизменны. */
  updateMedicalRecord = (id: string, patch: UpdateMedicalRecordRequest) =>
    this.put<MedicalRecord>(`/api/medical-records/${id}`, patch);

  shareMedicalRecord = (familyId: string) =>
    this.post<void>('/api/medical-records/share', { familyId });

  unshareMedicalRecord = (familyId: string) =>
    this.post<void>('/api/medical-records/unshare', { familyId });

  hideMedicalRecord = (recordId: string, familyIds: string[]) =>
    this.post<void>(`/api/medical-records/${recordId}/hide`, { familyIds });

  unhideMedicalRecord = (recordId: string, familyIds: string[]) =>
    this.post<void>(`/api/medical-records/${recordId}/unhide`, { familyIds });

  /** Безусловное удаление — сервер разрешает только владельцу (кто физически загрузил). */
  deleteMedicalRecord = (id: string) => this.del<void>(`/api/medical-records/${id}`);

  getAttachmentUrl = (id: string) => this.get<{ url: string }>(`/api/attachments/${id}/url`);

  /** Лимиты загрузки (до попытки — чтобы UI мог дизейблить кнопку/показать «осталось N из 8»). */
  getAttachmentLimits = () => this.get<AttachmentLimits>('/api/attachments/limits');

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

  // Конвейер извлечения показателей (ветка medicalrecords, редизайн v2) — одна кнопка
  // «Распознать» на ЗАПИСИ (обрабатывает все ещё не распознанные вложения последовательно),
  // статус/показатели/резюме записи, «мои показатели» + история для спарклайна.
  requestExtraction = (recordId: string) =>
    this.post<void>(`/api/medical-records/${recordId}/extract`);

  getExtractionStatus = (recordId: string) =>
    this.get<ExtractionStatusResponse>(`/api/medical-records/${recordId}/extraction`);

  getRecordIndicators = (recordId: string) =>
    this.get<IndicatorDto[]>(`/api/medical-records/${recordId}/indicators`);

  getRecordSummary = (recordId: string) =>
    this.get<RecordSummaryResponse>(`/api/medical-records/${recordId}/summary`);

  /** Заключение врача (Kind=DoctorVisit) — аналог getRecordSummary для Kind=Analysis. */
  getRecordConclusion = (recordId: string) =>
    this.get<VisitConclusion>(`/api/medical-records/${recordId}/conclusion`);

  /** Ручная правка показателя (ошибка OCR) — пересчитывает Flag на сервере. */
  updateIndicator = (id: string, patch: UpdateIndicatorRequest) =>
    this.put<void>(`/api/indicators/${id}`, patch);

  /** Ручное добавление показателя — без ожидания следующего «Распознать». */
  createIndicator = (recordId: string, body: CreateIndicatorRequest) =>
    this.post<IndicatorDto>(`/api/medical-records/${recordId}/indicators`, body);

  deleteIndicator = (id: string) => this.del<void>(`/api/indicators/${id}`);

  /** Последнее значение по каждому (показателю, биоматериалу) среди своих записей — /health/indicators. */
  getMyIndicators = () => this.get<MyIndicatorSummary[]>('/api/indicators');

  /** specimen/customId — query (не path), см. ExtractionEndpoints: второй ключ группировки не
   * помещается в path-сегмент. */
  getIndicatorHistory = (analyteKey: string, specimen: number, customId?: string | null) =>
    this.get<IndicatorHistoryPoint[]>(
      `/api/indicators/${encodeURIComponent(analyteKey)}${buildQuery({ specimen, customId: customId ?? undefined })}`,
    );

  /** Персонализированная статья справочника — панель/шторка справки по клику на показатель
   * (редизайн v2). */
  getIndicatorArticle = (indicatorId: string) =>
    this.get<IndicatorArticleResponse>(`/api/indicators/${indicatorId}/article`);

  /** Тренд показателя для КОНКРЕТНОЙ записи (редизайн v2) — в отличие от getIndicatorHistory
   * выше (строго "свои"), работает и для расшаренной чужой записи (двойной фильтр видимости
   * на сервере — см. GetRecordIndicatorHistoryAsync). */
  getRecordIndicatorHistory = (recordId: string, indicatorId: string) =>
    this.get<IndicatorHistoryPoint[]>(`/api/medical-records/${recordId}/indicators/${indicatorId}/history`);

  // Справочник показателей (редизайн v2) — зеркало searchKb/getKbMedication выше на другую таблицу.
  searchKbAnalytes = (q?: string, skip = 0, take = 20) => {
    const qQuery = q ? `&q=${encodeURIComponent(q)}` : '';
    return this.get<KbAnalyteListResponse>(`/api/kb/analytes?skip=${skip}&take=${take}${qQuery}`);
  };

  getKbAnalyte = (id: string) => this.get<KbAnalyteCard>(`/api/kb/analytes/${id}`);

  // Пользовательский справочник биоматериалов (UX-редизайн) — LLM-валидация один раз при создании.
  getSpecimens = () => this.get<UserSpecimen[]>('/api/specimens');

  createSpecimen = (name: string) => this.post<UserSpecimen>('/api/specimens', { name });

  // Оповещения
  getNotifications = (unreadOnly: boolean) =>
    this.get<AppNotification[]>(`/api/notifications?unreadOnly=${unreadOnly}`);
  markNotificationRead = (id: string) => this.post<void>(`/api/notifications/${id}/read`);

  /** Редизайн v2 — только счётчик для бейджа сайдбара/таба «Ещё». Отдельный эндпоинт вместо
   * getNotifications(true).length: список тянет полные (частично шифрованные) тела ради одного
   * числа, а бейдж опрашивается на каждом экране (см. NotificationStateService). */
  getUnreadNotificationCount = () => this.get<{ count: number }>('/api/notifications/unread-count');

  // Предпочтения доставки по типу оповещения (вкладка «Настройки → Уведомления»).
  getNotificationPreferences = () => this.get<NotificationPreference[]>('/api/notifications/preferences');
  saveNotificationPreferences = (prefs: NotificationPreference[]) =>
    this.put<void>('/api/notifications/preferences', prefs);

  // Поиск (этап 3): гибрид Postgres-FTS (лекарства, справочник) + in-memory (анализы) — см. SearchService.
  // types — опциональный серверный фильтр источников ("medication"/"kb"/"record", можно через
  // запятую); не запрошенный источник бэкенд вообще не трогает (см. SearchService.SearchAsync).
  search = (q: string, types?: string, page = 1, pageSize = 15) => {
    const typesQuery = types ? `&types=${encodeURIComponent(types)}` : '';
    return this.get<SearchResponse>(
      `/api/search?q=${encodeURIComponent(q)}${typesQuery}&page=${page}&pageSize=${pageSize}`,
    );
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
