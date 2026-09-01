// Вынесено из medical-records-panel.component.ts (редизайн v3, PR7) — переиспользуется и там
// (дозагрузка файлов к существующей записи), и в новом record-add.component.ts (форма создания).

import type { AttachmentLimits } from '../../models/types';

/** Зеркало FamilyHub.Infrastructure.Documents.DocumentContentTypes.All — то, что конвейер умеет
 * распознать (плюс .doc — хранится, но не распознаётся, см. докстринг там же). */
export const ACCEPTED_ATTACHMENT_TYPES = [
  'image/jpeg', 'image/png', 'image/webp', 'image/heic',
  'application/pdf', 'application/msword',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  'application/vnd.ms-excel', 'text/csv', 'text/plain', 'application/rtf', 'text/html',
].join(',');

export function formatMb(bytes: number): string {
  return `${(bytes / (1024 * 1024)).toFixed(1)} МБ`;
}

/** Общая клиентская предвалидация для формы создания и для дозагрузки к существующей записи —
 * лимиты те же (AttachmentUploadOptions), сервер всё равно перепроверит независимо. */
export function filterFilesAgainstLimits(
  limits: AttachmentLimits | null, existingCount: number, files: File[],
): { accepted: File[]; skippedByCount: number; tooLarge: File[] } {
  if (!limits) return { accepted: files, skippedByCount: 0, tooLarge: [] };

  const room = Math.max(0, limits.maxFilesPerRecord - existingCount);
  const withinCount = files.slice(0, room);
  const tooLarge = withinCount.filter((f) => f.size > limits.maxFileSizeBytes);
  const accepted = withinCount.filter((f) => f.size <= limits.maxFileSizeBytes);
  return { accepted, skippedByCount: files.length - withinCount.length, tooLarge };
}
