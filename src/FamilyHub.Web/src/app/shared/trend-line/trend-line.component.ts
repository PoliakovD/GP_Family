import { Component, computed, input } from '@angular/core';

const W = 200;
const H = 40;
const PAD_Y = 5;

/**
 * Мини-график тренда для плиток сводки дневника: полилиния по значениям + необязательная полоса
 * «обычного диапазона» (по истории самого пользователя, а не медицинская норма — дневник не
 * ставит диагнозов). Чистый inline-SVG без библиотек; отдельный от `shared/sparkline`, потому что
 * тот раскрашивает точки флагами нормы анализов, а здесь флагов нет.
 */
@Component({
  selector: 'app-trend-line',
  template: `
    <svg [attr.viewBox]="'0 0 ' + w + ' ' + h" preserveAspectRatio="none" class="trend-line" role="img"
         [attr.aria-label]="label()">
      @if (bandRect(); as b) {
        <rect x="0" [attr.y]="b.y" [attr.width]="w" [attr.height]="b.height" class="trend-band" />
      }
      @if (points().length > 1) {
        <polyline [attr.points]="polyline()" class="trend-path" />
      }
      @if (last(); as l) {
        <circle [attr.cx]="l.x" [attr.cy]="l.y" r="3" class="trend-dot" />
      }
    </svg>
  `,
  styles: [`
    :host { display: block; }
    .trend-line { width: 100%; height: 38px; display: block; overflow: visible; }
    .trend-band { fill: var(--color-accent-100); }
    .trend-path { fill: none; stroke: var(--trend-color, var(--color-accent)); stroke-width: 2; vector-effect: non-scaling-stroke; }
    .trend-dot { fill: var(--trend-color, var(--color-accent)); }
  `],
})
export class TrendLineComponent {
  readonly values = input.required<number[]>();
  /** Нижняя и верхняя граница полосы в тех же единицах, что и values. */
  readonly band = input<[number, number] | null>(null);
  readonly label = input('График динамики');

  protected readonly w = W;
  protected readonly h = H;

  private readonly range = computed(() => {
    const vs = this.values();
    const b = this.band();
    const all = b ? [...vs, b[0], b[1]] : vs;
    if (all.length === 0) return { min: 0, max: 1 };
    const min = Math.min(...all);
    const max = Math.max(...all);
    // Плоский ряд не должен схлопываться в деление на ноль — рисуется ровной линией по центру.
    return max === min ? { min: min - 1, max: max + 1 } : { min, max };
  });

  private y(v: number): number {
    const { min, max } = this.range();
    return H - PAD_Y - ((v - min) / (max - min)) * (H - 2 * PAD_Y);
  }

  protected readonly points = computed(() => {
    const vs = this.values();
    const step = vs.length > 1 ? W / (vs.length - 1) : 0;
    return vs.map((v, i) => ({ x: i * step, y: this.y(v) }));
  });

  protected readonly polyline = computed(() =>
    this.points().map((p) => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' '));

  protected readonly last = computed(() => this.points().at(-1) ?? null);

  protected readonly bandRect = computed(() => {
    const b = this.band();
    if (!b) return null;
    const top = this.y(b[1]);
    return { y: top, height: Math.max(2, this.y(b[0]) - top) };
  });
}
