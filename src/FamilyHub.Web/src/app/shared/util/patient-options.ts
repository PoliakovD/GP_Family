// Вынесено из medical-records-panel.component.ts (редизайн v3, PR7) — переиспользуется и там
// (фильтр «Пациент» в списке), и в новом record-add.component.ts (форма создания записи), одна
// и та же логика "я + подопечные + другие активные участники моих активных семей".

import type { FamilySummary } from '../../models/types';
import { formatPersonName } from './person-name';

/** Одна опция «Чей анализ» — либо «Я» (оба id null), либо подопечный семьи, либо другой активный
 * участник. Составной строковый key нужен для [(ngModel)]/чипов на UI. */
export interface PatientOption {
  key: string;
  familyDependentId: string | null;
  targetUserId: string | null;
  label: string;
}

export const SELF_PATIENT_OPTION: PatientOption = { key: 'self', familyDependentId: null, targetUserId: null, label: 'Я' };

/** «Я» + все подопечные и все другие активные участники из активных семей (дедуп по userId —
 * участник может состоять сразу в нескольких общих семьях). */
export function buildPatientOptions(activeFamilies: readonly FamilySummary[], myUserId: string | undefined): PatientOption[] {
  const options: PatientOption[] = [SELF_PATIENT_OPTION];
  const seenUserIds = new Set<string>(myUserId ? [myUserId] : []);

  for (const family of activeFamilies) {
    for (const dep of family.dependents ?? []) {
      options.push({
        key: `dep:${dep.id}`,
        familyDependentId: dep.id,
        targetUserId: null,
        label: `${dep.isPet ? dep.firstName : formatPersonName(dep, 'full')} (${family.name})`,
      });
    }
    for (const member of family.currentMembers ?? []) {
      if (seenUserIds.has(member.id)) continue;
      seenUserIds.add(member.id);
      options.push({
        key: `user:${member.id}`,
        familyDependentId: null,
        targetUserId: member.id,
        label: `${formatPersonName(member, 'full')} (${family.name})`,
      });
    }
  }
  return options;
}
