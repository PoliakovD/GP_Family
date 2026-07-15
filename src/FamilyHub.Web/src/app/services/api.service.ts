import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type {
  AppNotification,
  Attachment,
  Birthday,
  BirthdayInput,
  FamilySummary,
  InviteCreated,
  MedicalRecord,
  MedicalRecordInput,
  Medication,
  MedicationInput,
  PendingMember,
} from '../models/types';
import { FamilyRole } from '../models/types';

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

  private async get<T>(path: string): Promise<T> {
    try {
      return await firstValueFrom(this.http.get<T>(path));
    } catch (e) {
      throw this.toApiError(e);
    }
  }

  private async post<T>(path: string, body: unknown = null): Promise<T> {
    try {
      const result = await firstValueFrom(this.http.post<T>(path, body));
      return result as T;
    } catch (e) {
      throw this.toApiError(e);
    }
  }

  private async put<T>(path: string, body: unknown = null): Promise<T> {
    try {
      const result = await firstValueFrom(this.http.put<T>(path, body));
      return result as T;
    } catch (e) {
      throw this.toApiError(e);
    }
  }

  private async del<T>(path: string): Promise<T> {
    try {
      const result = await firstValueFrom(this.http.delete<T>(path));
      return result as T;
    } catch (e) {
      throw this.toApiError(e);
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

  // Аптечка
  getMedications = (familyId: string) => this.get<Medication[]>(`/api/families/${familyId}/medications`);
  createMedication = (familyId: string, input: MedicationInput) =>
    this.post<Medication>(`/api/families/${familyId}/medications`, input);
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
    const formData = new FormData();
    formData.append('file', file);
    try {
      return await firstValueFrom(
        this.http.post<Attachment>(`/api/medical-records/${recordId}/attachments`, formData),
      );
    } catch (e) {
      throw this.toApiError(e);
    }
  }

  // Оповещения
  getNotifications = (unreadOnly: boolean) =>
    this.get<AppNotification[]>(`/api/notifications?unreadOnly=${unreadOnly}`);
  markNotificationRead = (id: string) => this.post<void>(`/api/notifications/${id}/read`);
}
