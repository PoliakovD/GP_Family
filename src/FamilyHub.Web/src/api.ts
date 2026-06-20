import { getInitData } from './telegram';
import {
  FamilyRole,
  type AppNotification,
  type Attachment,
  type Birthday,
  type BirthdayInput,
  type FamilySummary,
  type InviteCreated,
  type MedicalRecord,
  type MedicalRecordInput,
  type Medication,
  type MedicationInput,
  type PendingMember,
} from './types';

export class ApiError extends Error {
  status: number;

  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

const DEV_TG_ID_KEY = 'familyhub:devTgId';

// В обычном браузере (без Telegram) initData пуста — подставляем X-Dev-TelegramId,
// который понимает DevAuthenticationHandler/"Smart"-схема на API (только Development).
function devTelegramId(): string | null {
  const fromQuery = new URLSearchParams(window.location.search).get('devTgId');
  if (fromQuery) {
    localStorage.setItem(DEV_TG_ID_KEY, fromQuery);
    return fromQuery;
  }
  return localStorage.getItem(DEV_TG_ID_KEY);
}

function authHeaders(): HeadersInit {
  const initData = getInitData();
  if (initData) {
    return { Authorization: `tma ${initData}` };
  }

  const devId = devTelegramId();
  return devId ? { 'X-Dev-TelegramId': devId } : {};
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { ...authHeaders(), ...(init?.headers ?? {}) },
  });

  if (!response.ok) {
    const text = await response.text().catch(() => '');
    throw new ApiError(response.status, text || response.statusText);
  }

  if (response.status === 204) {
    return undefined as T;
  }
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

function withJsonBody(method: string, body?: unknown): RequestInit {
  return {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  };
}

// --- Семьи, инвайты, участники ---

export const getFamilies = () => request<FamilySummary[]>('/api/families');

export const createFamily = (name: string) =>
  request<{ id: string }>('/api/families', withJsonBody('POST', { name }));

export const getPendingMembers = (familyId: string) =>
  request<PendingMember[]>(`/api/families/${familyId}/pending`);

export const approveMember = (familyId: string, userId: string) =>
  request<void>(`/api/families/${familyId}/members/${userId}/approve`, withJsonBody('POST'));

export const rejectMember = (familyId: string, userId: string) =>
  request<void>(`/api/families/${familyId}/members/${userId}/reject`, withJsonBody('POST'));

export const createInvite = (familyId: string) =>
  request<InviteCreated>(
    `/api/families/${familyId}/invites`,
    withJsonBody('POST', { targetUserId: null, assignedRole: FamilyRole.Member, maxUses: 1, expiresAt: null }),
  );

export const redeemInvite = (code: string) =>
  request<{ status: string }>(`/api/invites/${code}/redeem`, withJsonBody('POST'));

// --- Аптечка ---

export const getMedications = (familyId: string) =>
  request<Medication[]>(`/api/families/${familyId}/medications`);

export const createMedication = (familyId: string, input: MedicationInput) =>
  request<Medication>(`/api/families/${familyId}/medications`, withJsonBody('POST', input));

export const updateMedication = (medicationId: string, input: MedicationInput) =>
  request<void>(`/api/medications/${medicationId}`, withJsonBody('PUT', input));

export const deleteMedication = (medicationId: string) =>
  request<void>(`/api/medications/${medicationId}`, withJsonBody('DELETE'));

// --- Дни рождения ---

export const getBirthdays = (familyId: string) =>
  request<Birthday[]>(`/api/families/${familyId}/birthdays`);

export const createBirthday = (familyId: string, input: BirthdayInput) =>
  request<Birthday>(`/api/families/${familyId}/birthdays`, withJsonBody('POST', input));

export const updateBirthday = (birthdayId: string, input: BirthdayInput) =>
  request<void>(`/api/birthdays/${birthdayId}`, withJsonBody('PUT', input));

export const deleteBirthday = (birthdayId: string) =>
  request<void>(`/api/birthdays/${birthdayId}`, withJsonBody('DELETE'));

// --- Анализы и вложения ---

export const getMedicalRecords = () => request<MedicalRecord[]>('/api/medical-records');

export const createMedicalRecord = (input: MedicalRecordInput) =>
  request<MedicalRecord>('/api/medical-records', withJsonBody('POST', input));

export const shareMedicalRecord = (familyId: string) =>
  request<void>('/api/medical-records/share', withJsonBody('POST', { familyId }));

export const unshareMedicalRecord = (familyId: string) =>
  request<void>('/api/medical-records/unshare', withJsonBody('POST', { familyId }));

export const hideMedicalRecord = (recordId: string, familyIds: string[]) =>
  request<void>(`/api/medical-records/${recordId}/hide`, withJsonBody('POST', { familyIds }));

export const unhideMedicalRecord = (recordId: string, familyIds: string[]) =>
  request<void>(`/api/medical-records/${recordId}/unhide`, withJsonBody('POST', { familyIds }));

export const uploadAttachment = async (recordId: string, file: File): Promise<Attachment> => {
  const formData = new FormData();
  formData.append('file', file);
  const response = await fetch(`/api/medical-records/${recordId}/attachments`, {
    method: 'POST',
    headers: authHeaders(),
    body: formData,
  });
  if (!response.ok) {
    throw new ApiError(response.status, await response.text().catch(() => response.statusText));
  }
  return response.json();
};

export const getAttachmentUrl = (attachmentId: string) =>
  request<{ url: string }>(`/api/attachments/${attachmentId}/url`);

// --- Оповещения ---

export const getNotifications = (unreadOnly: boolean) =>
  request<AppNotification[]>(`/api/notifications?unreadOnly=${unreadOnly}`);

export const markNotificationRead = (id: string) =>
  request<void>(`/api/notifications/${id}/read`, withJsonBody('POST'));
