import { ChangeDetectionStrategy, Component, ElementRef, Renderer2, effect, inject, input } from '@angular/core';
import type { LucideIconData } from 'lucide-angular';

const SVG_NS = 'http://www.w3.org/2000/svg';

/**
 * Иконка Lucide (DESIGN §2.7: линейные, stroke 1.8, скруглённые концы, 20px). Одна библиотека на
 * всё приложение — Phosphor уходит последним PR фазы 4.
 *
 * Данные иконки импортируются поимённо (`import { House } from 'lucide-angular'`) — в бандл
 * попадают только используемые. Своя обёртка, а не компонент пакета: тот собран под Angular 13
 * (NgModule), а здесь нужны только SVG-узлы из данных.
 *
 * Без `label` иконка декоративная (aria-hidden) — подпись несёт соседний текст или aria-label
 * кнопки. С `label` — role="img" и aria-label (иконка — единственный носитель смысла).
 */
@Component({
  selector: 'app-icon',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
  host: {
    class: 'app-icon',
    '[attr.aria-hidden]': 'label() ? null : "true"',
    '[attr.role]': 'label() ? "img" : null',
    '[attr.aria-label]': 'label()',
    '[style.--icon-box]': 'size() + "px"',
  },
  styles: [`
    :host {
      display: inline-flex;
      flex: none;
      width: var(--icon-box, var(--icon-size));
      height: var(--icon-box, var(--icon-size));
      color: inherit;
    }
    :host ::ng-deep svg { width: 100%; height: 100%; display: block; }
  `],
})
export class IconComponent {
  readonly icon = input.required<LucideIconData>();
  readonly size = input<number>(20);
  readonly strokeWidth = input<number>(1.8);
  readonly label = input<string | null>(null);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly renderer = inject(Renderer2);

  constructor() {
    effect(() => {
      const el = this.host.nativeElement;
      while (el.firstChild) el.removeChild(el.firstChild);

      const svg = this.renderer.createElement('svg', SVG_NS) as SVGElement;
      const attrs: Record<string, string> = {
        viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor',
        'stroke-width': String(this.strokeWidth()), 'stroke-linecap': 'round', 'stroke-linejoin': 'round',
        focusable: 'false',
      };
      for (const [k, v] of Object.entries(attrs)) this.renderer.setAttribute(svg, k, v);

      for (const [tag, nodeAttrs] of this.icon()) {
        const child = this.renderer.createElement(tag, SVG_NS);
        for (const [k, v] of Object.entries(nodeAttrs)) {
          if (k !== 'key') this.renderer.setAttribute(child, k, String(v));
        }
        this.renderer.appendChild(svg, child);
      }
      this.renderer.appendChild(el, svg);
    });
  }
}
