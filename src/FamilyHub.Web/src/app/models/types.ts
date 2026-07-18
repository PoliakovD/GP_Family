// Типы зеркалят DTO бэкенда (System.Text.Json, минимальные API): свойства camelCase,
// enum'ы без JsonStringEnumConverter сериализуются как целые числа — см. FamilyHub.Domain.Enums.*.

export const FamilyRole = {Member: 0, Admin: 1} as const;
export const MemberStatus = {PendingApproval: 0, Active: 1} as const;
export const RemoveMemberResult = {
    Removed: 0,
    Forbidden: 1,
    NotFound: 2,
    LastAdmin: 3
} as const;
export type RemoveMemberResult = typeof RemoveMemberResult[keyof typeof RemoveMemberResult];

export const NotificationType = {
    MedicationExpiringSoon: 0,
    MedicationExpired: 1,
    BirthdayUpcoming: 2,
} as const;

export interface FamilySummary {
    id: string;
    name: string;
    myRole: number; // FamilyRole admin or member
    myStatus: number; // MemberStatus // active or pending to be active
    currentMembers: CurrentMember[] | null;
}

export interface PendingMember {
    userId: string;
    displayName: string;
    username: string | null;
    role: number; // FamilyRole
    joinedAt: string;
}


export interface CurrentMember {
    id: string;
    displayName: string;
    username: string | null;
    role: number; // FamilyRole
    joinedAt: string;
}


export interface InviteCreated {
    id: string;
    code: string;
    maxUses: number;
    expiresAt: string | null;
    telegramLink: string | null;
}

export interface Medkit {
    id: string;
    familyId: string;
    name: string;
    createdByUserId: string;
    createdAt: string;
}

export interface MedkitInput {
    name: string;
}

export interface Medication {
    id: string;
    medkitId: string;
    familyId: string;
    name: string;
    expiryDate: string | null; // DateOnly "yyyy-MM-dd"
    // Всё остальное про медикамент — единым JSON: instructions, quantity (известные ключи,
    // под них в форме отдельные привычные инпуты) + что найдёт оцифровка по фото (manufacturer,
    // type, dose, mainActingAgent, любые доп. находки).
    data: Record<string, string>;
    createdByUserId: string;
    createdAt: string;
}

export interface MedicationInput {
    name: string;
    expiryDate: string | null;
    data: Record<string, string>;
}

/** Ответ POST /api/medications/ocr — результат оцифровки медикамента по фото локальной LLM. */
export interface MedicationOcrResponse {
    success: boolean;
    name: string | null;
    expiryDate: string | null; // dd/MM/yyyy, как вернула модель — конвертируется на фронте
    data: Record<string, string> | null;
    error: string | null;
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
