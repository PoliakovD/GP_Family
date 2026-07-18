import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import {
  AppNotification,
  Attachment,
  Birthday,
  BirthdayInput, CurrentMember,
  FamilySummary,
  InviteCreated,
  MedicalRecord,
  MedicalRecordInput,
  Medication,
  MedicationInput,
  Medkit,
  MedkitInput,
  PendingMember,
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
  getPendingMembers = (familyId: string) => this.get<PendingMember[]>(`/api/families/${familyId}/pending`);
  getCurrentMembers = (familyId: string) => this.get<CurrentMember[]>(`/api/families/${familyId}/current`);
  approveMember = (familyId: string, userId: string) =>
    this.post<void>(`/api/families/${familyId}/members/${userId}/approve`);
  rejectMember = (familyId: string, userId: string) =>
    this.post<void>(`/api/families/${familyId}/members/${userId}/reject`);
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

  // Дни рождения
  getBirthdays = (familyId: string) => this.get<Birthday[]>(`/api/families/${familyId}/birthdays`);
  createBirthday = (familyId: string, input: BirthdayInput) =>
    this.post<Birthday>(`/api/families/${familyId}/birthdays`, input);
  updateBirthday = (id: string, input: BirthdayInput) =>
    this.put<void>(`/api/birthdays/${id}`, input);
  deleteBirthday = (id: string) => this.del<void>(`/api/birthdays/${id}`);

  // Анализы и вложения
  getMedicalRecords = () => this.get<MedicalRecord[]>('/api/medical-records');
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
}
