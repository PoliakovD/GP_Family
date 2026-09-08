import {
  Component, ElementRef, EventEmitter, HostListener, Input, OnChanges, OnDestroy, Output,
  SimpleChanges, ViewChild, inject,
} from '@angular/core';
import { ApiService } from '../../services/api.service';
import { TelegramService } from '../../services/telegram.service';
import { BreakpointService } from '../../services/breakpoint.service';
import type { Attachment } from '../../models/types';
import { HistoryDismissController } from '../util/history-dismiss';
import { OverlayA11y } from '../util/overlay-a11y';
import { ZoomPanController, type ZoomPanTransform } from './zoom-pan';
import { buildLocalViewerItem, fromAttachmentPreview, pendingViewerItem, type ViewerItem } from './file-viewer.types';
import { ImagePreviewComponent } from './renderers/image-preview.component';
import { PdfPreviewComponent } from './renderers/pdf-preview.component';
import { TextPreviewComponent } from './renderers/text-preview.component';
import { UnsupportedPreviewComponent } from './renderers/unsupported-preview.component';

const PREVIEW_POLL_INTERVAL_MS = 1500;

/**
 * Полноэкранный просмотрщик вложений — карточка анализа/врача, детали записи, сразу после
 * загрузки файла (см. attachment-list.component.ts). Две обёртки на одном содержимом
 * (`.claude/patterns/frontend_web.md`): `wide` — крупный центрированный оверлей, `narrow`/`medium`
 * — во весь экран без рамки/бэкдропа (тот же приём, что record-add.component).
 *
 * Источник — либо список вложений записи (`attachments`+`activeIndex`, обычный режим: тянет
 * /preview с сервера, поллит, пока PreviewStatus=Pending), либо один локальный файл (`localFile`,
 * запись ещё не создана — record-add до отправки формы): рендерит blob:-URL без похода на сервер.
 *
 * Жесты — один контроллер (ZoomPanController) на весь `.viewer-stage`: колесо/пинч — зум,
 * драг при зуме — панорама, горизонтальный свайп БЕЗ зума — переключение файла в записи
 * (гарантированно не конфликтует с постраничной навигацией PDF — та отдельными кнопками/
 * PageUp-PageDown внутри app-pdf-preview, см. её докстринг).
 */
@Component({
  selector: 'app-file-viewer',
  standalone: true,
  imports: [ImagePreviewComponent, PdfPreviewComponent, TextPreviewComponent, UnsupportedPreviewComponent],
  templateUrl: './file-viewer.component.html',
  styleUrl: './file-viewer.component.scss',
})
export class FileViewerComponent implements OnChanges, OnDestroy {
  @Input() open = false;
  @Input() attachments: Attachment[] = [];
  @Input() activeIndex = 0;
  /** Режим локального (ещё не загруженного) файла — если задан, `attachments`/`activeIndex`
   * игнорируются, галереи и скачивания нет. */
  @Input() localFile: File | null = null;
  @Output() closed = new EventEmitter<void>();
  @Output() activeIndexChange = new EventEmitter<number>();

  @ViewChild('overlayRoot') private overlayRoot?: ElementRef<HTMLElement>;
  @ViewChild(PdfPreviewComponent) private pdfPreview?: PdfPreviewComponent;

  private readonly api = inject(ApiService);
  private readonly tg = inject(TelegramService);
  private readonly breakpoints = inject(BreakpointService);
  private readonly history = new HistoryDismissController(() => this.closed.emit());
  private readonly a11y = new OverlayA11y();
  private readonly zoomPan = new ZoomPanController((t) => (this.transform = t));

  transform: ZoomPanTransform = { scale: 1, x: 0, y: 0, rotationDeg: 0 };
  activeItem: ViewerItem | null = null;

  private readonly itemCache = new Map<string, ViewerItem>();
  private pollHandle: ReturnType<typeof setInterval> | null = null;
  private loadToken = 0;
  private localObjectUrl: string | null = null;
  private dragStart: { x: number; y: number; time: number } | null = null;

  get isWide(): boolean {
    return this.breakpoints.tier() === 'wide';
  }

  get hasGallery(): boolean {
    return !this.localFile && this.attachments.length > 1;
  }

  get canTransform(): boolean {
    return this.activeItem?.status === 'ready' && (this.activeItem.renderKind === 'image' || this.activeItem.renderKind === 'pdf');
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['open']) {
      this.history.sync(this.open);
      if (this.open) {
        queueMicrotask(() => this.overlayRoot && this.a11y.activate(this.overlayRoot.nativeElement));
      } else {
        this.a11y.deactivate();
        this.clearLocalObjectUrl();
      }
    }
    if (this.open && (changes['open'] || changes['activeIndex'] || changes['localFile'] || changes['attachments'])) {
      this.zoomPan.reset();
      void this.loadActive();
    }
  }

  ngOnDestroy(): void {
    this.history.destroy();
    this.a11y.deactivate();
    this.clearPoll();
    this.clearLocalObjectUrl();
  }

  @HostListener('window:popstate')
  onPopState(): void {
    this.history.onPopState(this.open);
  }

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    if (!this.open) return;
    switch (event.key) {
      case 'Escape':
        this.requestClose();
        break;
      case 'ArrowLeft':
        this.prev();
        break;
      case 'ArrowRight':
        this.next();
        break;
      case '+':
      case '=':
        if (this.canTransform) this.zoomPan.zoomBy(1.25);
        break;
      case '-':
        if (this.canTransform) this.zoomPan.zoomBy(1 / 1.25);
        break;
      case '0':
        if (this.canTransform) this.zoomPan.reset();
        break;
      case 'r':
      case 'R':
        if (this.canTransform) this.zoomPan.rotate90();
        break;
      case 'Tab':
        if (this.overlayRoot) this.a11y.trapTab(event, this.overlayRoot.nativeElement);
        break;
    }
  }

  requestClose(): void {
    this.closed.emit();
  }

  prev(): void {
    if (this.hasGallery && this.activeIndex > 0) this.activeIndexChange.emit(this.activeIndex - 1);
  }

  next(): void {
    if (this.hasGallery && this.activeIndex < this.attachments.length - 1) this.activeIndexChange.emit(this.activeIndex + 1);
  }

  rotate(): void {
    if (this.canTransform) this.zoomPan.rotate90();
  }

  zoomIn(): void {
    if (this.canTransform) this.zoomPan.zoomBy(1.25);
  }

  zoomOut(): void {
    if (this.canTransform) this.zoomPan.zoomBy(1 / 1.25);
  }

  download(): void {
    const url = this.activeItem?.downloadUrl;
    if (url) this.tg.openExternalLink(url);
  }

  print(): void {
    const item = this.activeItem;
    if (!item?.contentUrl) return;

    if (item.renderKind === 'pdf' && this.pdfPreview) {
      this.pdfPreview.print();
      return;
    }
    if (item.renderKind === 'image') this.printImage(item.contentUrl);
  }

  // --- Жесты (см. докстринг класса) ---

  onStageWheel(event: WheelEvent): void {
    if (this.canTransform) this.zoomPan.onWheel(event);
  }

  onStagePointerDown(event: PointerEvent): void {
    if (this.canTransform) this.zoomPan.onPointerDown(event);
    this.dragStart = { x: event.clientX, y: event.clientY, time: Date.now() };
  }

  onStagePointerMove(event: PointerEvent): void {
    if (this.canTransform) this.zoomPan.onPointerMove(event);
  }

  onStagePointerUp(event: PointerEvent): void {
    const wasPan = this.canTransform && this.zoomPan.isZoomed && this.zoomPan.wasDragged(event);
    if (this.canTransform) this.zoomPan.onPointerUp(event);

    if (!wasPan && this.dragStart && this.hasGallery) {
      const dx = event.clientX - this.dragStart.x;
      const dy = event.clientY - this.dragStart.y;
      const dt = Date.now() - this.dragStart.time;
      if (Math.abs(dx) > 60 && Math.abs(dx) > Math.abs(dy) * 1.5 && dt < 600) {
        if (dx < 0) this.next();
        else this.prev();
      }
    }
    this.dragStart = null;
  }

  // --- Загрузка активного элемента ---

  private async loadActive(): Promise<void> {
    this.clearPoll();
    const token = ++this.loadToken;

    if (this.localFile) {
      this.clearLocalObjectUrl();
      this.localObjectUrl = URL.createObjectURL(this.localFile);
      this.activeItem = buildLocalViewerItem(this.localFile, this.localObjectUrl);
      return;
    }

    const attachment = this.attachments[this.activeIndex];
    if (!attachment) {
      this.activeItem = null;
      return;
    }

    this.activeItem = this.itemCache.get(attachment.id) ?? pendingViewerItem(attachment);

    try {
      const preview = await this.api.getAttachmentPreview(attachment.id);
      if (token !== this.loadToken) return; // перелистнули, пока грузили
      const item = fromAttachmentPreview(attachment.id, preview);
      this.itemCache.set(attachment.id, item);
      this.activeItem = item;
      if (item.status === 'pending') this.startPolling(attachment.id, token);
    } catch {
      if (token !== this.loadToken) return;
      this.activeItem = { ...pendingViewerItem(attachment), status: 'failed', failureReason: 'Не удалось загрузить превью.' };
    }
  }

  private startPolling(attachmentId: string, token: number): void {
    this.pollHandle = setInterval(async () => {
      try {
        const preview = await this.api.getAttachmentPreview(attachmentId);
        if (token !== this.loadToken) {
          this.clearPoll();
          return;
        }
        const item = fromAttachmentPreview(attachmentId, preview);
        this.itemCache.set(attachmentId, item);
        this.activeItem = item;
        if (item.status !== 'pending') this.clearPoll();
      } catch {
        this.clearPoll();
      }
    }, PREVIEW_POLL_INTERVAL_MS);
  }

  private clearPoll(): void {
    if (this.pollHandle !== null) {
      clearInterval(this.pollHandle);
      this.pollHandle = null;
    }
  }

  private clearLocalObjectUrl(): void {
    if (this.localObjectUrl) {
      URL.revokeObjectURL(this.localObjectUrl);
      this.localObjectUrl = null;
    }
  }

  private printImage(url: string): void {
    const iframe = document.createElement('iframe');
    iframe.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0';
    document.body.appendChild(iframe);
    const doc = iframe.contentDocument;
    if (!doc) {
      iframe.remove();
      return;
    }
    doc.open();
    doc.write('<html><body style="margin:0"></body></html>');
    doc.close();
    const img = doc.createElement('img');
    img.style.maxWidth = '100%';
    img.onload = () => {
      iframe.contentWindow?.focus();
      iframe.contentWindow?.print();
      setTimeout(() => iframe.remove(), 60_000);
    };
    img.src = url; // DOM-присвоение, не срока HTML — сигнатура ссылки безопасна без экранирования
    doc.body.appendChild(img);
  }
}
