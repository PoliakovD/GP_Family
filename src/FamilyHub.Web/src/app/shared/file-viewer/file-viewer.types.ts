// Общая модель "что показать" — одна и та же что для уже загруженного вложения (сервер решил
// renderKind и выдал подписанные ссылки, см. AttachmentPreview в models/types.ts), что для ещё
// не отправленного локального файла (ViewerItem строится на клиенте, см. buildLocalViewerItem).
// Рендереры (renderers/*) знают только про ViewerItem, не про Attachment/File/HTTP.

import type { Attachment, AttachmentPreview } from '../../models/types';
import { AttachmentPreviewStatus, AttachmentRenderKind } from '../../models/types';
import { describeFile } from '../util/file-type';

export type ViewerRenderKind = 'none' | 'pdf' | 'image' | 'text';
export type ViewerStatus = 'pending' | 'ready' | 'unsupported' | 'failed';

export interface ViewerItem {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  status: ViewerStatus;
  renderKind: ViewerRenderKind;
  pageCount: number | null;
  thumbnailUrl: string | null;
  /** URL, который рисуют pdf.js/&lt;img&gt;/fetch — signed-ссылка сервера (AllowAnonymous, см.
   * DownloadTokenService) или blob:-URL локального файла. Ни то, ни другое не требует
   * authInterceptor — можно передавать в &lt;img src&gt;/pdf.js напрямую, минуя HttpClient. */
  contentUrl: string | null;
  /** Отсутствует для ещё не загруженного локального файла — скачивать нечего, он уже на устройстве. */
  downloadUrl: string | null;
  failureReason: string | null;
}

const RENDER_KIND_MAP: Record<AttachmentRenderKind, ViewerRenderKind> = {
  [AttachmentRenderKind.None]: 'none',
  [AttachmentRenderKind.Pdf]: 'pdf',
  [AttachmentRenderKind.Image]: 'image',
  [AttachmentRenderKind.Text]: 'text',
};

export function fromAttachmentPreview(attachmentId: string, preview: AttachmentPreview): ViewerItem {
  const status: ViewerStatus =
    preview.status === AttachmentPreviewStatus.Pending || preview.status === AttachmentPreviewStatus.None
      ? 'pending'
      : preview.status === AttachmentPreviewStatus.Ready
        ? 'ready'
        : preview.status === AttachmentPreviewStatus.Failed
          ? 'failed'
          : 'unsupported';

  return {
    id: attachmentId,
    fileName: preview.fileName,
    contentType: preview.contentType,
    sizeBytes: preview.sizeBytes,
    status,
    renderKind: RENDER_KIND_MAP[preview.renderKind],
    pageCount: preview.pageCount,
    thumbnailUrl: preview.thumbnailUrl,
    contentUrl: preview.contentUrl,
    downloadUrl: preview.downloadUrl,
    failureReason: preview.failureReason,
  };
}

/** Пока /preview ещё не ответил (первое открытие) — рисуем то немногое, что уже известно из
 * списка вложений (см. Attachment.previewStatus), чтобы не мигать пустым спиннером без подписи. */
export function pendingViewerItem(attachment: Attachment): ViewerItem {
  return {
    id: attachment.id,
    fileName: attachment.fileName,
    contentType: attachment.contentType,
    sizeBytes: attachment.sizeBytes,
    status: 'pending',
    renderKind: 'none',
    pageCount: null,
    thumbnailUrl: null,
    contentUrl: null,
    downloadUrl: null,
    failureReason: null,
  };
}

const LOCAL_IMAGE_TYPES = new Set(['image/jpeg', 'image/png', 'image/webp']);
const LOCAL_TEXT_TYPES = new Set(['text/plain', 'text/csv', 'application/xml', 'text/xml']);

/** Локальный (ещё не загруженный) файл — например, из record-add до отправки формы. HEIC/TIFF
 * браузер сам не отрисует (нет серверной нормализации до аплоада) — остаются карточкой
 * «Формат…», а не падают в разбитую картинку; тот же файл откроется полноценно сразу после
 * загрузки, когда сервер сгенерирует Page-артефакт. Вызывающий код владеет object URL
 * (revokeObjectURL при закрытии вьюера/удалении файла) — эта функция только строит описание. */
export function buildLocalViewerItem(file: File, objectUrl: string): ViewerItem {
  const contentType = file.type;
  let renderKind: ViewerRenderKind = 'none';
  if (contentType === 'application/pdf') renderKind = 'pdf';
  else if (LOCAL_IMAGE_TYPES.has(contentType)) renderKind = 'image';
  else if (LOCAL_TEXT_TYPES.has(contentType)) renderKind = 'text';

  return {
    id: `local:${file.name}:${file.size}:${file.lastModified}`,
    fileName: file.name,
    contentType,
    sizeBytes: file.size,
    status: renderKind === 'none' ? 'unsupported' : 'ready',
    renderKind,
    pageCount: null,
    thumbnailUrl: null,
    contentUrl: renderKind === 'none' ? null : objectUrl,
    downloadUrl: null,
    failureReason: renderKind === 'none' ? describeFile(contentType, file.name).label : null,
  };
}
