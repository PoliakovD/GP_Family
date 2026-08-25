import { Component, computed, input } from '@angular/core';

/** Одна точка спарклайна — числовое значение показателя + флаг отклонения на эту дату. */
export interface SparklinePoint {
  value: number;
  flag: number; // IndicatorFlag
}

const FLAG_COLOR: Record<number, string> = {
  0: '#adb5bd', // Unknown — нейтральный серый
  1: '#f08c00', // Low — тот же оранжевый, что expiry-warning (medications-panel)
  2: '#2f9e44', // Normal — тот же зелёный, что expiry-ok
  3: '#f08c00', // High
  4: '#e03131', // Critical — тот же красный, что expiry-danger
};

const WIDTH = 240;
const HEIGHT = 48;
const PADDING = 6;

/**
 * Мини-график тренда показателя — inline SVG, без библиотек графиков (в проекте её нет и тащить
 * ради спарклайна не нужно, см. план ветки medicalrecords). Точки красятся по IndicatorFlag —
 * видно не только тренд, но и где было отклонение.
 */
@Component({
  selector: 'app-sparkline',
  standalone: true,
  template: `
    @if (points().length > 0) {
      <svg [attr.viewBox]="'0 0 ' + width + ' ' + height" [attr.width]="width" [attr.height]="height" role="img" [attr.aria-label]="ariaLabel()">
        @if (points().length > 1) {
          <polyline [attr.points]="polylinePoints()" fill="none" stroke="var(--color-accent)" stroke-width="1.5" />
        }
        @for (p of coords(); track $index) {
          <circle [attr.cx]="p.x" [attr.cy]="p.y" r="3" [attr.fill]="p.color" />
        }
      </svg>
    } @else {
      <span class="muted" style="font-size:12px">Недостаточно данных</span>
    }
  `,
})
export class SparklineComponent {
  readonly points = input<SparklinePoint[]>([]);

  readonly width = WIDTH;
  readonly height = HEIGHT;

  private readonly bounds = computed(() => {
    const values = this.points().map((p) => p.value);
    const min = Math.min(...values);
    const max = Math.max(...values);
    return { min, max, span: max - min || 1 };
  });

  readonly coords = computed(() => {
    const pts = this.points();
    const { min, span } = this.bounds();
    const innerWidth = WIDTH - PADDING * 2;
    const innerHeight = HEIGHT - PADDING * 2;
    const step = pts.length > 1 ? innerWidth / (pts.length - 1) : 0;

    return pts.map((p, i) => ({
      x: PADDING + step * i,
      y: PADDING + innerHeight - ((p.value - min) / span) * innerHeight,
      color: FLAG_COLOR[p.flag] ?? FLAG_COLOR[0],
    }));
  });

  readonly polylinePoints = computed(() => this.coords().map((c) => `${c.x},${c.y}`).join(' '));

  readonly ariaLabel = computed(() => `Динамика показателя: ${this.points().map((p) => p.value).join(', ')}`);
}
