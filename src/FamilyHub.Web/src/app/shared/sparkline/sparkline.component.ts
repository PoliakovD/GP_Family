import { Component, computed, input, ChangeDetectionStrategy } from '@angular/core';

/** Одна точка спарклайна — числовое значение показателя + флаг отклонения на эту дату. */
export interface SparklinePoint {
  value: number;
  flag: number; // IndicatorFlag
}

// Цвет точки по смыслу (DESIGN §1 п.2): вне нормы (ниже/выше/критично) — danger, норма — success,
// нет оценки — muted. Токены — через [style.fill]: атрибут fill не понимает var().
const FLAG_COLOR: Record<number, string> = {
  0: 'var(--color-text-muted)', // Unknown
  1: 'var(--color-danger)', // Low
  2: 'var(--color-success)', // Normal
  3: 'var(--color-danger)', // High
  4: 'var(--color-danger)', // Critical
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
  changeDetection: ChangeDetectionStrategy.Eager,
  template: `
    @if (points().length > 0) {
      <svg [attr.viewBox]="'0 0 ' + width + ' ' + height" [attr.width]="width" [attr.height]="height" role="img" [attr.aria-label]="ariaLabel()">
        @if (points().length > 1) {
          <polyline [attr.points]="polylinePoints()" fill="none" [style.stroke]="'var(--color-text-secondary)'" stroke-width="1.5" />
        }
        @for (p of coords(); track $index) {
          <circle [attr.cx]="p.x" [attr.cy]="p.y" r="3" [style.fill]="p.color" />
        }
      </svg>
    } @else {
      <span class="form-hint">Недостаточно данных</span>
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
