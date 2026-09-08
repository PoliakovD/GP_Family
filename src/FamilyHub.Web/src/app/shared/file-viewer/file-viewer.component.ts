import {
  Component, ElementRef, EventEmitter, HostListener, Input, OnChanges, OnDestroy, Output,
  SimpleChanges, ViewChild, inject,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
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
 * Просмотрщик вложений — карточка анализа/врача, детали записи, сразу после загрузки файла (см.
 * attachment-list.component.ts). Две обёртки на одном содержимом (`.claude/patterns/frontend_web.md`),
 * но НЕ обе модальные:
 * - `wide` (десктоп) — плавающее НЕМОДАЛЬНОЕ окно (`.viewer-window`): без бэкдропа, без блокировки
 *   скролла/фокус-трапа, перетаскивается за шапку, растягивается за угол (нативный CSS `resize`).
 *   Пользователь может одновременно продолжать редактировать показатели/создавать записи в фоне —
 *   это и есть смысл фичи, поэтому клик мимо окна его НЕ закрывает (в отличие от modal/bottom-sheet).
 * - `narrow`/`medium` — модальный оверлей во весь экран (тот же приём, что record-add.component) —
 *   там перекрывать нечего, экран и так один на всё.
 *
 * Источник — либо список вложений записи (`attachments`+`activeIndex`, обычный режим: тянет
 * /preview с сервера, поллит, пока PreviewStatus=Pending), либо один локальный файл (`localFile`,
 * запись ещё не создана — record-add до отправки формы): рендерит blob:-URL без похода на сервер.
 *
 * Жесты — один контроллер (ZoomPanController) на весь `.viewer-stage`: колесо/пинч — зум, драг
 * при зуме — панорама. Горизонтальный свайп БЕЗ зума — постранично листает открытый PDF, пока
 * есть куда (ожидаемый жест для чтения документа); только на границе (первая/последняя страница)
 * или для непостраничного файла свайп переключает на следующий/предыдущий ФАЙЛ записи (см.
 * onStagePointerUp) — кнопки/PageUp-PageDown внутри app-pdf-preview делают то же самое явно,
 * жест не заменяет их, а дублирует. Драг за шапку (только `wide`) — отдельный, независимый
 * жест: двигает само окно, не контент.
 *
 * Глобальные клавиши (стрелки/зум/Escape) активны, только когда фокус НЕ находится в поле ввода
 * за пределами вьюера — необходимо именно из-за немодальности `wide`: пользователь может в этот
 * момент печатать значение показателя в форме позади плавающего окна.
 */
@Component({
  selector: 'app-file-viewer',
  standalone: true,
  imports: [NgTemplateOutlet, ImagePreviewComponent, PdfPreviewComponent, TextPreviewComponent, UnsupportedPreviewComponent],
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

  /** Смещение плавающего окна (`wide`) от его якорной позиции (см. .viewer-window в CSS) —
   * двигается драгом за шапку, сбрасывается при каждом открытии вьюера. */
  windowOffset = { x: 0, y: 0 };
  private windowDrag: { startX: number; startY: number; originX: number; originY: number } | null = null;

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
        this.windowOffset = { x: 0, y: 0 };
        // Фокус-трап/блокировка скролла — только для модального fullscreen (narrow/medium).
        // `wide` — плавающее НЕМОДАЛЬНОЕ окно (см. докстринг класса): страница за ним должна
        // остаться прокручиваемой и доступной для Tab, иначе смысла в "не блокирует экран" нет.
        if (!this.isWide) {
          queueMicrotask(() => this.overlayRoot && this.a11y.activate(this.overlayRoot.nativeElement));
        }
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
    // wide — окно немодальное: пользователь может в этот момент печатать значение показателя в
    // форме ЗА окном (см. докстринг класса) — стрелки/+/-/r там не должны перехватываться вьюером.
    // Пока фокус внутри самого окна, шорткаты работают как обычно.
    const target = event.target as HTMLElement | null;
    const isTypingOutsideViewer =
      !!target
      && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)
      && !(this.overlayRoot && this.overlayRoot.nativeElement.contains(target));
    if (isTypingOutsideViewer) return;

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
        // Фокус-трап есть только у модального fullscreen (narrow/medium) — wide не запирает Tab
        // внутри плавающего окна, иначе снова не даёт пользоваться остальной страницей.
        if (!this.isWide && this.overlayRoot) this.a11y.trapTab(event, this.overlayRoot.nativeElement);
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

  // --- Перетаскивание плавающего окна (только wide, см. докстринг класса) ---

  onHeaderPointerDown(event: PointerEvent): void {
    if (!this.isWide) return;
    // Клик по кнопке в шапке (закрыть/скачать/зум…) не должен запускать драг окна.
    if ((event.target as HTMLElement).closest('button')) return;
    this.windowDrag = { startX: event.clientX, startY: event.clientY, originX: this.windowOffset.x, originY: this.windowOffset.y };
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
  }

  @HostListener('document:pointermove', ['$event'])
  onWindowDragMove(event: PointerEvent): void {
    if (!this.windowDrag) return;
    this.windowOffset = {
      x: this.windowDrag.originX + (event.clientX - this.windowDrag.startX),
      y: this.windowDrag.originY + (event.clientY - this.windowDrag.startY),
    };
  }

  @HostListener('document:pointerup')
  @HostListener('document:pointercancel')
  onWindowDragEnd(): void {
    this.windowDrag = null;
  }

  /** Двойной клик по шапке — вернуть окно на якорную позицию (см. .viewer-window в CSS),
   * если утащили его куда-то неудобно и не хочется искать глазами край экрана. */
  resetWindow(): void {
    this.windowOffset = { x: 0, y: 0 };
  }

  // --- Жесты содержимого (зум/панорама/свайп-галерея, см. докстринг класса) ---

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

    const dragStart = this.dragStart;
    this.dragStart = null;
    if (wasPan || !dragStart) return;

    const dx = event.clientX - dragStart.x;
    const dy = event.clientY - dragStart.y;
    const dt = Date.now() - dragStart.time;
    const isSwipe = Math.abs(dx) > 60 && Math.abs(dx) > Math.abs(dy) * 1.5 && dt < 600;
    if (!isSwipe) return;

    // Пока в открытом документе есть куда листать — свайп листает СТРАНИЦЫ (ожидаемый жест для
    // чтения многостраничного PDF); к следующему/предыдущему ФАЙЛУ записи свайп переходит только
    // на границе (последняя/первая страница) или если файл вообще не постраничный.
    const pdf = this.pdfPreview;
    if (pdf && pdf.pageCount > 1) {
      if (dx < 0 && pdf.pageNumber < pdf.pageCount) {
        pdf.nextPage();
        return;
      }
      if (dx > 0 && pdf.pageNumber > 1) {
        pdf.prevPage();
        return;
      }
    }

    if (this.hasGallery) {
      if (dx < 0) this.next();
      else this.prev();
    }
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
