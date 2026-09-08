// Единая точка правды по "что это за файл" для чипов/списка вложений/вьюера — иконка Phosphor +
// человекочитаемая подпись типа + формат размера. ACCEPTED_ATTACHMENT_TYPES (attachment-upload.ts)
// остаётся отдельным списком (что можно ЗАГРУЗИТЬ) — этот модуль только про отображение.

export type FileKind = 'image' | 'pdf' | 'office-word' | 'office-excel' | 'text' | 'other';

export interface FileTypeInfo {
  kind: FileKind;
  /** Класс Phosphor-иконки (без веса — вызывающий код сам решает ph/ph-bold/ph-duotone). */
  icon: string;
  /** Короткая подпись типа для карточки «Скачать» (не путать с contentType). */
  label: string;
}

const IMAGE_TYPES = new Set(['image/jpeg', 'image/png', 'image/webp', 'image/heic', 'image/tiff']);
const WORD_TYPES = new Set([
  'application/msword',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
]);
const EXCEL_TYPES = new Set([
  'application/vnd.ms-excel',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
]);
const TEXT_TYPES = new Set(['text/plain', 'text/csv', 'application/xml', 'text/xml', 'application/rtf', 'text/html']);

export function describeFile(contentType: string, fileName: string): FileTypeInfo {
  const ct = contentType.toLowerCase();

  if (ct === 'application/pdf') return { kind: 'pdf', icon: 'ph-file-pdf', label: 'PDF' };
  if (IMAGE_TYPES.has(ct)) return { kind: 'image', icon: 'ph-file-image', label: 'Изображение' };
  if (WORD_TYPES.has(ct)) return { kind: 'office-word', icon: 'ph-file-doc', label: 'Документ Word' };
  if (EXCEL_TYPES.has(ct)) return { kind: 'office-excel', icon: 'ph-file-xls', label: 'Таблица Excel' };
  if (TEXT_TYPES.has(ct)) return { kind: 'text', icon: 'ph-file-text', label: 'Текст' };

  const extension = fileName.includes('.') ? fileName.split('.').pop()!.toUpperCase() : null;
  return { kind: 'other', icon: 'ph-file', label: extension ?? 'Файл' };
}

/** КБ/МБ по размеру, а не всегда МБ — formatMb (attachment-upload.ts) остаётся для лимитов
 * («до 5.0 МБ»), где всегда уместна одна единица; здесь же файл может быть и 40 КБ. */
export function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} Б`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} КБ`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} МБ`;
}
