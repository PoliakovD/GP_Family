// Зеркалит FamilyHub.Domain.ValueObjects.PersonName — тот же приём, что PASSWORD_PATTERN/
// USERNAME_PATTERN зеркалят PasswordRules/UsernameRules (единый источник истины — сервер,
// здесь только форматирование для отображения + UX-валидация формы до отправки).

import type { BreakpointTier } from '../../services/breakpoint.service';

export type PersonNameStyle = 'full' | 'short-patronymic' | 'initials';

export interface PersonNameParts {
  lastName: string | null;
  firstName: string | null;
  middleName?: string | null;
}

/** ≥1024px — полное ФИО; 640–1023px — Фамилия Имя И.; <640px — Фамилия И.И. */
export function personNameStyleFor(tier: BreakpointTier): PersonNameStyle {
  switch (tier) {
    case 'wide': return 'full';
    case 'medium': return 'short-patronymic';
    case 'narrow': return 'initials';
  }
}

function initial(part: string): string {
  return part.length > 0 ? part[0].toUpperCase() : '';
}

/** Инициалы аватара из уже отформатированной строки имени (бэк не отдаёт ФИО отдельными полями
 * для мед-записи/показателя, только резолвленное отображаемое имя, см.
 * MedicalRecordService.ResolvePersonNamesAsync) — первые два токена в том порядке, в каком они
 * есть в строке; точный порядок (Фамилия/Имя) для инициалов не критичен, аватар чисто
 * декоративный. Вынесено из medical-records-panel.component.ts (редизайн v2.1) — та же логика
 * нужна и в indicators-tab (строки «Показателей»), см. app-person-chip. */
export function personAvatarPartsFromName(name: string): { firstName: string; lastName: string | null } {
  const parts = name.trim().split(/\s+/);
  return { firstName: parts[0] ?? '', lastName: parts[1] ?? null };
}

/**
 * Редизайн v3 — «Анализы»: группа-человек показывает короткое имя всегда (не по брейкпойнту, как
 * formatPersonName выше), полное ФИО — только в title-тултипе. На вход — УЖЕ отформатированная
 * строка ("Фамилия Имя Отчество"), не структурные поля: бэк отдаёт мед-записям одно резолвленное
 * отображаемое имя (см. MedicalRecordService.ResolvePersonNamesAsync), а не ФИО по частям — та же
 * причина, по которой personAvatarParts в medical-records-panel.component.ts делает то же самое
 * простое разбиение по пробелу. "Иванов Иван Иванович" -> "Иванов И.И."; без отчества ->
 * "Иванов И."; один токен (например, кличка питомца) остаётся как есть.
 */
export function shortenDisplayName(fullName: string): string {
  const parts = fullName.trim().split(/\s+/).filter(Boolean);
  if (parts.length <= 1) return fullName.trim();
  const [last, ...rest] = parts;
  return `${last} ${rest.map((p) => `${initial(p)}.`).join('')}`;
}

/**
 * «Фамилия И.О.» для врача из свободного текстового поля записи (`MedicalRecord.doctor` — не
 * структурные поля, пользователь может ввести что угодно). Сокращаем только то, что похоже на ФИО:
 * 2–3 слова с заглавной буквы без точек; уже сокращённое («Иванов И.И.»), с должностью («терапевт
 * Иванов») или произвольный текст остаются как введены.
 */
export function shortenDoctorName(raw: string): string {
  const text = raw.trim();
  const parts = text.split(/\s+/);
  if (parts.length < 2 || parts.length > 3) return text;
  if (!parts.every((p) => /^[А-ЯЁA-Z][а-яёa-z-]+$/.test(p))) return text;
  return shortenDisplayName(text);
}

/** Схлопывается корректно без отчества (не у всех есть) во всех трёх стилях. */
export function formatPersonName(person: PersonNameParts, style: PersonNameStyle): string {
  const last = person.lastName?.trim() ?? '';
  const first = person.firstName?.trim() ?? '';
  const middle = person.middleName?.trim();
  const hasMiddle = !!middle;

  switch (style) {
    case 'full':
      return hasMiddle ? `${last} ${first} ${middle}` : `${last} ${first}`;
    case 'short-patronymic':
      return hasMiddle ? `${last} ${first} ${initial(middle!)}.` : `${last} ${first}`;
    case 'initials':
      return hasMiddle ? `${last} ${initial(first)}.${initial(middle!)}.` : `${last} ${initial(first)}.`;
  }
}
