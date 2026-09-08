import {
  AfterViewInit, Component, ElementRef, EventEmitter, HostListener, Input, OnChanges, OnDestroy,
  Output, SimpleChanges, ViewChild,
} from '@angular/core';
import type { PDFDocumentProxy } from 'pdfjs-dist';
import type { ZoomPanTransform } from '../zoom-pan';
import { LoadingSpinnerComponent } from '../../loading-spinner/loading-spinner.component';

let workerConfigured = false;

/**
 * Рендерит PDF через pdf.js, подключённый ЛЕНИВЫМ чанком (`await import('pdfjs-dist')`) — пакет
 * не должен попасть в initial-бандл (бюджет angular.json: warning 850kb/error 1.5mb), грузится
 * только когда реально открыт PDF/Office-документ (Office сводится к PDF на сервере — см.
 * AttachmentPreviewRenderer, здесь один и тот же рендерер закрывает оба случая).
 *
 * Постранично, не непрерывной прокруткой — свайп в file-viewer уже занят переключением МЕЖДУ
 * файлами записи (см. докстринг FileViewerComponent), поэтому листание страниц — отдельные
 * on-screen стрелки + клавиши PageUp/PageDown, без конфликта жестов.
 *
 * Зум — CSS-transform поверх один раз отрисованного при повышенном DPI канваса (см. baseScale),
 * не перерендер canvas на каждый шаг зума: дешевле и отзывчивее, а стартовая чёткость уже выше
 * логического размера страницы.
 */
@Component({
  selector: 'app-pdf-preview',
  standalone: true,
  imports: [LoadingSpinnerComponent],
  templateUrl: './pdf-preview.component.html',
  styleUrl: './pdf-preview.component.scss',
})
export class PdfPreviewComponent implements OnChanges, AfterViewInit, OnDestroy {
  @Input({ required: true }) url!: string;
  @Input({ required: true }) transform!: ZoomPanTransform;
  @Output() pageCountDetected = new EventEmitter<number>();

  @ViewChild('canvas') private canvasRef?: ElementRef<HTMLCanvasElement>;

  loading = true;
  error = false;
  pageNumber = 1;
  pageCount = 1;

  private doc: PDFDocumentProxy | null = null;
  private viewReady = false;
  /** Токен последнего запроса — конкурентная гонка при быстром переключении файлов не должна
   * дать устаревшему рендеру перезаписать уже актуальную страницу. */
  private renderToken = 0;

  async ngOnChanges(changes: SimpleChanges): Promise<void> {
    if (changes['url']) await this.loadDocument();
  }

  ngAfterViewInit(): void {
    this.viewReady = true;
    if (this.doc) void this.renderPage();
  }

  ngOnDestroy(): void {
    void this.doc?.destroy();
  }

  @HostListener('document:keydown.pagedown')
  onPageDownKey(): void {
    this.nextPage();
  }

  @HostListener('document:keydown.pageup')
  onPageUpKey(): void {
    this.prevPage();
  }

  nextPage(): void {
    if (this.pageNumber < this.pageCount) {
      this.pageNumber++;
      void this.renderPage();
    }
  }

  prevPage(): void {
    if (this.pageNumber > 1) {
      this.pageNumber--;
      void this.renderPage();
    }
  }

  print(): void {
    // См. FileViewerComponent.print() — печать всего документа проще и надёжнее через нативный
    // PDF-вьюер браузера в скрытом iframe, чем печатать текущий canvas одной страницы.
    const iframe = document.createElement('iframe');
    iframe.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0';
    iframe.src = this.url;
    iframe.onload = () => {
      iframe.contentWindow?.focus();
      iframe.contentWindow?.print();
      setTimeout(() => iframe.remove(), 60_000);
    };
    document.body.appendChild(iframe);
  }

  private async loadDocument(): Promise<void> {
    const token = ++this.renderToken;
    this.loading = true;
    this.error = false;
    this.pageNumber = 1;
    await this.doc?.destroy();
    this.doc = null;

    try {
      const pdfjsLib = await import('pdfjs-dist');
      if (!workerConfigured) {
        pdfjsLib.GlobalWorkerOptions.workerSrc = '/pdfjs/pdf.worker.min.mjs';
        workerConfigured = true;
      }
      const doc = await pdfjsLib.getDocument({ url: this.url }).promise;
      if (token !== this.renderToken) {
        void doc.destroy(); // переключились на другой файл, пока грузили этот
        return;
      }
      this.doc = doc;
      this.pageCount = doc.numPages;
      this.pageCountDetected.emit(doc.numPages);
      if (this.viewReady) await this.renderPage();
    } catch {
      if (token === this.renderToken) this.error = true;
    } finally {
      if (token === this.renderToken) this.loading = false;
    }
  }

  private async renderPage(): Promise<void> {
    const doc = this.doc;
    const canvas = this.canvasRef?.nativeElement;
    if (!doc || !canvas) return;

    const token = this.renderToken;
    try {
      const page = await doc.getPage(this.pageNumber);
      if (token !== this.renderToken) return;

      // Рендер при повышенном DPI (до 3x) один раз — дальше зум чисто CSS-трансформом (см.
      // докстринг класса), поэтому даже без device pixel ratio 2+ страница остаётся чёткой.
      const baseScale = 1.5 * Math.min(window.devicePixelRatio || 1, 2);
      const viewport = page.getViewport({ scale: baseScale });
      canvas.width = viewport.width;
      canvas.height = viewport.height;

      const context = canvas.getContext('2d');
      if (!context) return;
      await page.render({ canvasContext: context, viewport }).promise;
    } catch {
      if (token === this.renderToken) this.error = true;
    }
  }
}
