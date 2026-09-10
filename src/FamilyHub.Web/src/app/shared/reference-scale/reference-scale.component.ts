import { Component, computed, input } from '@angular/core';
import { IndicatorFlag } from '../../models/types';

const SIZE = {
  full: { width: 300, height: 40, padding: 12, trackHeight: 10 },
  mini: { width: 170, height: 20, padding: 8, trackHeight: 6 },
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
 * Шкала-референс (редизайн v2, переработана в v2.2) — горизонтальная полоса с зоной нормы
 * [low, high] и палочкой текущего значения. Inline SVG (как sparkline) — нужна точная геометрия
 * засечки, а библиотеки графиков в проекте нет и заводить её ради одного компонента не стоит.
 *
 * Редизайн v2.2 — заменили тонкую линию-трек с круглой засечкой на толстый закрашенный трек и
 * палочку (rect), как в референсе: полоса шире и читается лучше на маленьких экранах, а палочка
 * не путается визуально с точками sparkline (тоже круги). Ровно два цвета палочки (не четыре,
 * как раньше у markerColor) — тёмно-зелёный, когда показатель Normal, иначе тёмно-красный: по
 * явному запросу «бордоватая или темно зеленоватая», не по градации Low/High/Critical. Клип за
 * пределы домена больше не рисует отдельную стрелку — палочка у края читается как «за пределами»
 * без специального случая (см. geometry() — просто clamp позиции).
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
        <rect [attr.x]="g.pad" [attr.y]="g.trackY" [attr.width]="g.innerWidth" [attr.height]="g.trackHeight"
              [attr.rx]="g.trackHeight / 2" class="rs-track" />
        <rect [attr.x]="g.normX1" [attr.y]="g.trackY" [attr.width]="g.normWidth" [attr.height]="g.trackHeight"
              [attr.rx]="g.trackHeight / 2" class="rs-norm" />
        <rect [attr.x]="g.stickX" [attr.y]="g.stickY" [attr.width]="g.stickWidth" [attr.height]="g.stickHeight"
              rx="1.5" [class]="stickClass()" />
      </svg>
    }
  `,
  styles: [`
    .rs-track { fill: var(--color-neutral-200); }
    .rs-norm { fill: color-mix(in srgb, var(--color-status-ok) 32%, var(--color-neutral-200)); }
    .rs-stick-ok { fill: var(--color-status-ok-text); }
    .rs-stick-bad { fill: var(--color-status-danger-text); }
  `],
})
export class ReferenceScaleComponent {
  readonly value = input.required<number | null>();
  readonly low = input.required<number | null>();
  readonly high = input.required<number | null>();
  readonly unit = input<string | null>(null);
  readonly variant = input<'full' | 'mini'>('full');

  /** IndicatorFlag — только для цвета палочки; сама зона нормы всегда зелёная (это она задаёт
   * "что такое норма", а не текущий статус значения). Ровно два состояния — см. докстринг класса. */
  readonly flag = input<number>(IndicatorFlag.Normal);

  readonly stickClass = computed(() =>
    this.flag() === IndicatorFlag.Normal ? 'rs-stick-ok' : 'rs-stick-bad',
  );

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

    const { width, height, padding, trackHeight } = SIZE[this.variant()];
    const mid = height / 2;
    const innerWidth = width - padding * 2;
    const stickWidth = 3;
    const stickHeight = trackHeight + 6; // нависает по 3px над/под треком с обеих сторон

    const span = high - low || Math.abs(high) || 1; // low===high — вырожденный случай, не деление на 0
    const domainLow = low - span * 0.5;
    const domainHigh = high + span * 0.5;
    const domainSpan = domainHigh - domainLow;

    const toX = (v: number) => padding + ((v - domainLow) / domainSpan) * innerWidth;

    const normX1 = toX(low);
    const normX2 = toX(high);

    const v = value ?? low; // нет значения (не должно случиться на практике) — палочка на границе, не крах
    // Клип за пределы домена — просто прижимаем к краю трека (без отдельной стрелки, см. докстринг).
    const stickCenterX = Math.min(Math.max(toX(v), padding + stickWidth / 2), width - padding - stickWidth / 2);

    return {
      width, height, pad: padding,
      trackY: mid - trackHeight / 2, trackHeight, innerWidth,
      normX1, normWidth: normX2 - normX1,
      stickX: stickCenterX - stickWidth / 2, stickY: mid - stickHeight / 2, stickWidth, stickHeight,
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
