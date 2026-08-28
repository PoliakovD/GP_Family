import { Component, computed, input } from '@angular/core';

const SIZE = {
  full: { width: 280, height: 32, padding: 10, markerR: 5 },
  mini: { width: 120, height: 16, padding: 6, markerR: 3 },
};

/**
 * Форматирует отклонение значения от границы диапазона для человека, не для отладки: "выше
 * нормы на 12" / "ниже нормы на 0,8". Не в самом компоненте — переиспользуется индикатор-инфо
 * (строка "Текущее значение") и таблицей показателей без необходимости монтировать SVG.
 */
export function formatDeviation(value: number, low: number | null, high: number | null): string | null {
  if (low !== null && value < low) return `ниже нормы на ${formatNumber(low - value)}`;
  if (high !== null && value > high) return `выше нормы на ${formatNumber(value - high)}`;
  return null;
}

function formatNumber(n: number): string {
  // До двух знаков, без хвостовых нулей — "0,8", не "0,80"; запятая — русская локаль отображения.
  return (Math.round(n * 100) / 100).toString().replace('.', ',');
}

/**
 * Шкала-референс (редизайн v2) — горизонтальная полоса с зоной нормы [low, high] и засечкой
 * текущего значения. Inline SVG (как sparkline) — нужна точная геометрия засечки, а библиотеки
 * графиков в проекте нет и заводить её ради одного компонента не стоит.
 *
 * Деградация — штатный случай, не ошибка: если low или high не заданы (RefSource.None/
 * качественный результат без числа), компонент рендерит null. Вызывающая сторона обязана сама
 * решить, что показать вместо шкалы (refText или прочерк) — компонент не пытается угадать.
 */
@Component({
  selector: 'app-reference-scale',
  standalone: true,
  template: `
    @if (geometry(); as g) {
      <svg
        [attr.viewBox]="'0 0 ' + g.width + ' ' + g.height"
        [attr.width]="g.width"
        [attr.height]="g.height"
        role="img"
        [attr.aria-label]="ariaLabel()"
      >
        <line [attr.x1]="g.pad" [attr.y1]="g.mid" [attr.x2]="g.width - g.pad" [attr.y2]="g.mid"
              stroke="var(--color-divider)" stroke-width="2" stroke-linecap="round" />
        <line [attr.x1]="g.normX1" [attr.y1]="g.mid" [attr.x2]="g.normX2" [attr.y2]="g.mid"
              stroke="var(--color-status-ok)" stroke-width="2" stroke-linecap="round" />
        @if (g.clippedLeft) {
          <path [attr.d]="g.arrowLeftPath" [attr.fill]="markerColor()" />
        } @else if (g.clippedRight) {
          <path [attr.d]="g.arrowRightPath" [attr.fill]="markerColor()" />
        } @else {
          <circle [attr.cx]="g.markerX" [attr.cy]="g.mid" [attr.r]="g.markerR"
                   [attr.fill]="markerColor()" stroke="var(--color-paper)" stroke-width="1.5" />
        }
      </svg>
    }
  `,
})
export class ReferenceScaleComponent {
  readonly value = input.required<number | null>();
  readonly low = input.required<number | null>();
  readonly high = input.required<number | null>();
  readonly unit = input<string | null>(null);
  readonly variant = input<'full' | 'mini'>('full');

  /** IndicatorFlag — только для цвета засечки; сама зона нормы всегда зелёная (это она задаёт
   * "что такое норма", а не текущий статус значения). */
  readonly flag = input<number>(2);

  readonly markerColor = computed(() => {
    switch (this.flag()) {
      case 1: case 3: return 'var(--color-status-warning)';
      case 4: return 'var(--color-status-danger)';
      case 0: return 'var(--color-status-none)';
      default: return 'var(--color-status-ok)';
    }
  });

  readonly deviationLabel = computed(() => {
    const v = this.value();
    if (v === null) return null;
    return formatDeviation(v, this.low(), this.high());
  });

  readonly geometry = computed(() => {
    const low = this.low();
    const high = this.high();
    const value = this.value();
    if (low === null || high === null) return null;

    const { width, height, padding, markerR } = SIZE[this.variant()];
    const mid = height / 2;
    const innerWidth = width - padding * 2;

    const span = high - low || Math.abs(high) || 1; // low===high — вырожденный случай, не деление на 0
    const domainLow = low - span * 0.5;
    const domainHigh = high + span * 0.5;
    const domainSpan = domainHigh - domainLow;

    const toX = (v: number) => padding + ((v - domainLow) / domainSpan) * innerWidth;

    const normX1 = toX(low);
    const normX2 = toX(high);

    const v = value ?? low; // нет значения (не должно случиться на практике) — засечка на границе, не крах
    const clippedLeft = v < domainLow;
    const clippedRight = v > domainHigh;
    const markerX = clippedLeft ? padding : clippedRight ? width - padding : toX(v);

    return {
      width, height, pad: padding, mid, markerR, normX1, normX2, markerX,
      clippedLeft, clippedRight,
      arrowLeftPath: arrowPath(padding, mid, markerR, 'left'),
      arrowRightPath: arrowPath(width - padding, mid, markerR, 'right'),
    };
  });

  readonly ariaLabel = computed(() => {
    const v = this.value();
    const unit = this.unit() ?? '';
    const deviation = this.deviationLabel();
    if (v === null) return 'Шкала нормы';
    return `Значение ${v}${unit ? ' ' + unit : ''}${deviation ? ', ' + deviation : ', в норме'}`;
  });
}

function arrowPath(tipX: number, midY: number, r: number, direction: 'left' | 'right'): string {
  const dx = direction === 'left' ? r * 1.4 : -r * 1.4;
  return `M ${tipX} ${midY} L ${tipX + dx} ${midY - r} L ${tipX + dx} ${midY + r} Z`;
}
