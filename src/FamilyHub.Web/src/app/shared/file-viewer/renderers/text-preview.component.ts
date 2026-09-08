import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { LoadingSpinnerComponent } from '../../loading-spinner/loading-spinner.component';

type TextKind = 'plain' | 'csv' | 'xml';

/** txt/csv/xml не получают серверных превью-артефактов вовсе (см. AttachmentPreviewRenderer —
 * незачем растрировать то, что и так текст) — этот компонент сам тянет содержимое по contentUrl
 * (signed-ссылка или blob: для локального файла, см. ViewerItem) и решает, как его показать. */
@Component({
  selector: 'app-text-preview',
  standalone: true,
  imports: [CommonModule, LoadingSpinnerComponent],
  templateUrl: './text-preview.component.html',
  styleUrl: './text-preview.component.scss',
})
export class TextPreviewComponent implements OnChanges {
  @Input({ required: true }) url!: string;
  @Input({ required: true }) contentType!: string;

  loading = true;
  error = false;
  kind: TextKind = 'plain';
  rawText = '';
  csvRows: string[][] = [];

  async ngOnChanges(changes: SimpleChanges): Promise<void> {
    if (!changes['url'] && !changes['contentType']) return;
    this.loading = true;
    this.error = false;
    this.kind = this.detectKind(this.contentType);

    try {
      // fetch(), не HttpClient — contentUrl анонимный и подписанный (или blob: локального файла),
      // authInterceptor тут ни при чём (см. докстринг ViewerItem.contentUrl).
      const response = await fetch(this.url);
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const text = await response.text();

      if (this.kind === 'csv') {
        this.csvRows = parseCsv(text).slice(0, 500);
      } else if (this.kind === 'xml') {
        this.rawText = prettyPrintXml(text);
      } else {
        this.rawText = text;
      }
    } catch {
      this.error = true;
    } finally {
      this.loading = false;
    }
  }

  private detectKind(contentType: string): TextKind {
    const ct = contentType.toLowerCase();
    if (ct === 'text/csv') return 'csv';
    if (ct === 'application/xml' || ct === 'text/xml') return 'xml';
    return 'plain';
  }
}

function parseCsv(text: string): string[][] {
  return text
    .split(/\r\n|\n|\r/)
    .filter((line) => line.length > 0)
    .map((line) => line.split(','));
}

/** Наивный форматтер отступов — не полноценный XML-парсер (незачем тянуть библиотеку ради
 * просмотра), просто переносит строку и отступ на каждый открывающий/закрывающий тег. Ломается на
 * CDATA/смешанном контенте — в этом случае вьюер просто покажет менее аккуратный, но читаемый текст. */
function prettyPrintXml(xml: string): string {
  const collapsed = xml.replace(/>\s*</g, '><').trim();
  let indent = 0;
  const lines: string[] = [];
  for (const token of collapsed.split(/(?=<)/)) {
    if (!token) continue;
    if (/^<\/.+>/.test(token)) indent = Math.max(0, indent - 1);
    lines.push('  '.repeat(indent) + token);
    if (/^<[^/!?][^>]*[^/]>$/.test(token)) indent++;
  }
  return lines.join('\n');
}
