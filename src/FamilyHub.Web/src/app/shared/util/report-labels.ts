// Подписи и форматирование раздела «Отчёты для врача».

import { DoctorReport, DoctorReportLinkStatus } from '../../models/types';
import { MONTHS_NOM, parseLocalBirthDate } from './birthday-date';
import { formatDayMonth } from './date-format';
import { dayKey } from './health-note-labels';
import { pluralizeRu } from './pluralize';

/** Сроки публичной ссылки — те же значения принимает бэкенд (DoctorReportService.AllowedShareDays). */
export const SHARE_DAY_OPTIONS = [7, 14, 30] as const;
export const DEFAULT_SHARE_DAYS = 14;

/** «Март — сентябрь 2026», «Сентябрь 2026», «Декабрь 2025 — февраль 2026». */
export function periodTitle(from: string, to: string): string {
  const f = parseLocalBirthDate(from);
  const t = parseLocalBirthDate(to);
  const first = MONTHS_NOM[f.getMonth()];
  const last = MONTHS_NOM[t.getMonth()].toLowerCase();
  if (f.getFullYear() === t.getFullYear()) {
    return f.getMonth() === t.getMonth() ? `${first} ${f.getFullYear()}` : `${first} — ${last} ${f.getFullYear()}`;
  }
  return `${first} ${f.getFullYear()} — ${last} ${t.getFullYear()}`;
}

/** Адрес публичной страницы врача. Токен — в пути, а не в query: не попадает в логи как параметр. */
export function reportUrl(token: string): string {
  return `${location.origin}/r/${token}`;
}

/** ISO-момент → «8 октября» (локальная дата). */
export function dayMonthFromIso(iso: string): string {
  return formatDayMonth(dayKey(iso));
}

/** Когда истечёт ссылка, выданная сегодня на N дней («10 октября»). */
export function expiryPreview(days: number, now: Date = new Date()): string {
  return dayMonthFromIso(new Date(now.getTime() + days * 86_400_000).toISOString());
}

export function blocksLabel(n: number): string {
  return `${n} ${pluralizeRu(n, 'блок', 'блока', 'блоков')}`;
}

export function timesLabel(n: number): string {
  return `${n} ${pluralizeRu(n, 'раз', 'раза', 'раз')}`;
}

export type LinkTone = 'active' | 'expired' | 'revoked' | 'none';

export interface LinkView {
  tone: LinkTone;
  label: string;
  /** Класс иконки Phosphor целиком. */
  icon: string;
  caption: string;
}

/** Статус ссылки — главная колонка списка: пользователь должен видеть, у кого открыты его данные. */
export function linkView(report: DoctorReport): LinkView {
  const link = report.link;
  switch (link.status) {
    case DoctorReportLinkStatus.Active:
      return {
        tone: 'active',
        label: 'Ссылка активна',
        icon: 'ph-fill ph-link-simple',
        caption: `до ${link.expiresAt ? dayMonthFromIso(link.expiresAt) : '—'} · ${
          link.viewCount === 0 ? 'ещё не открывали' : `открыта ${timesLabel(link.viewCount)}`}`,
      };
    case DoctorReportLinkStatus.Expired:
      return {
        tone: 'expired',
        label: 'Ссылка истекла',
        icon: 'ph ph-clock-countdown',
        caption: link.expiresAt ? dayMonthFromIso(link.expiresAt) : '',
      };
    case DoctorReportLinkStatus.Revoked:
      return {
        tone: 'revoked',
        label: 'Доступ отозван',
        icon: 'ph ph-link-break',
        caption: link.revokedAt ? `вами, ${dayMonthFromIso(link.revokedAt)}` : 'вами',
      };
    default:
      return { tone: 'none', label: 'Только PDF', icon: 'ph ph-file-pdf', caption: 'ссылка не создавалась' };
  }
}
