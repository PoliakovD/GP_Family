// Типы зеркалят DTO бэкенда (System.Text.Json, минимальные API): свойства camelCase,
// enum'ы без JsonStringEnumConverter сериализуются как целые числа — см. FamilyHub.Domain.Enums.*.

export const FamilyRole = { Member: 0, Admin: 1 } as const;
export const MemberStatus = { PendingApproval: 0, Active: 1 } as const;
export const NotificationType = {
  MedicationExpiringSoon: 0,
  MedicationExpired: 1,
  BirthdayUpcoming: 2,
} as const;

export interface FamilySummary {
  id: string;
  name: string;
  myRole: number; // FamilyRole
  myStatus: number; // MemberStatus
}

export interface PendingMember {
  userId: string;
  role: number; // FamilyRole
  joinedAt: string;
}

export interface InviteCreated {
  id: string;
  code: string;
  maxUses: number;
  expiresAt: string | null;
}

export interface Medication {
  id: string;
  familyId: string;
  name: string;
  instructions: string | null;
  expiryDate: string | null; // DateOnly "yyyy-MM-dd"
  quantity: number;
  createdByUserId: string;
  createdAt: string;
}

export interface MedicationInput {
  name: string;
  instructions: string | null;
  expiryDate: string | null;
  quantity: number;
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

export interface MedicalRecord {
  id: string;
  ownerUserId: string;
  personName: string;
  recordDate: string;
  doctor: string | null;
  description: string | null;
  createdAt: string;
}

export interface MedicalRecordInput {
  personName: string;
  recordDate: string;
  doctor: string | null;
  description: string | null;
  hideFromFamilyIds: string[] | null;
}

export interface Attachment {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedAt: string;
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
