import { Component, Input, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { Meta } from '@angular/platform-browser';
import { ApiError, ApiService } from '../../services/api.service';
import { PublicReportMeta } from '../../models/types';
import { PdfPreviewComponent } from '../../shared/file-viewer/renderers/pdf-preview.component';
import { ZoomPanTransform } from '../../shared/file-viewer/zoom-pan';

type State = 'loading' | 'ready' | 'gone' | 'error';

const ZOOM_STEPS = [1, 1.25, 1.5, 2];

/** «2026-09-26» → «26.09.2026». */
const dmy = (date: string) => date.split('-').reverse().join('.');

/**
 * Страница врача по публичной ссылке (`/r/:token`, БЕЗ гардов и без оболочки приложения) — документ, а
 * не приложение: кто пациент, за какой период, до какого числа доступна ссылка, предупреждение и сам
 * PDF. Неверная, истёкшая и отозванная ссылка выглядят ОДИНАКОВО и ничего не раскрывают (ни имени, ни
 * периода) — врачу сказано только, что делать.
 */
@Component({
  selector: 'app-public-report',
  imports: [PdfPreviewComponent],
  templateUrl: './public-report.component.html',
  styleUrl: './public-report.component.scss',
})
export class PublicReportComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly meta = inject(Meta);

  @Input() token = '';

  protected readonly state = signal<State>('loading');
  protected readonly report = signal<PublicReportMeta | null>(null);
  protected readonly pdfUrl = signal<string | null>(null);
  protected readonly transform = signal<ZoomPanTransform>({ scale: 1, x: 0, y: 0, rotationDeg: 0 });
  protected readonly zoomLabel = signal('100%');

  private blob: Blob | null = null;
  private zoomIndex = 0;
  private readonly tags: HTMLMetaElement[] = [];

  ngOnInit(): void {
    // Страница не для поисковиков; токен в адресе не должен утекать в Referer.
    for (const tag of [{ name: 'robots', content: 'noindex, nofollow' }, { name: 'referrer', content: 'no-referrer' }]) {
      const el = this.meta.addTag(tag);
      if (el) this.tags.push(el);
    }
    void this.load();
  }

  ngOnDestroy(): void {
    this.tags.forEach((t) => t.remove());
    const url = this.pdfUrl();
    if (url) URL.revokeObjectURL(url);
  }

  private async load(): Promise<void> {
    try {
      this.report.set(await this.api.getPublicReport(this.token));
      // PDF загружается сразу: именно этот запрос фиксирует «открытие» ссылки на стороне пациента.
      this.blob = await this.api.getPublicReportPdf(this.token);
      this.pdfUrl.set(URL.createObjectURL(this.blob));
      this.state.set('ready');
    } catch (e) {
      // 404 — один и тот же ответ для неизвестной, истёкшей и отозванной ссылки.
      this.report.set(null);
      this.state.set(e instanceof ApiError && e.status === 404 ? 'gone' : 'error');
    }
  }

  protected zoom(direction: 1 | -1): void {
    this.zoomIndex = Math.min(ZOOM_STEPS.length - 1, Math.max(0, this.zoomIndex + direction));
    const scale = ZOOM_STEPS[this.zoomIndex];
    this.transform.set({ scale, x: 0, y: 0, rotationDeg: 0 });
    this.zoomLabel.set(`${Math.round(scale * 100)}%`);
  }

  protected download(): void {
    const r = this.report();
    if (!this.blob || !r) return;
    const url = URL.createObjectURL(this.blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `otchet-dlya-vracha-${r.periodFrom}-${r.periodTo}.pdf`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
  }

  protected reload(): void {
    this.state.set('loading');
    void this.load();
  }

  protected period(r: PublicReportMeta): string {
    return `${dmy(r.periodFrom)} — ${dmy(r.periodTo)}`;
  }

  protected date(iso: string): string {
    const d = new Date(iso);
    return `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}.${d.getFullYear()}`;
  }

  /** «м, 28 лет · 04.05.1998». */
  protected birthLine(r: PublicReportMeta): string {
    const who = [r.sex, r.age === null ? null : `${r.age} ${this.years(r.age)}`].filter(Boolean).join(', ');
    return [who, r.birthDate ? dmy(r.birthDate) : null].filter(Boolean).join(' · ');
  }

  private years(n: number): string {
    const m100 = n % 100;
    const m10 = n % 10;
    if (m100 >= 11 && m100 <= 14) return 'лет';
    return m10 === 1 ? 'год' : m10 >= 2 && m10 <= 4 ? 'года' : 'лет';
  }
}
