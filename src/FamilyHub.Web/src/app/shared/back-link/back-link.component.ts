import { Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ChevronLeft } from 'lucide-angular';
import { IconComponent } from '../ui/icon/icon.component';

/**
 * Редизайн v2.2 — «‹ Название раздела» вместо голой кнопки-иконки без подписи (была на экране
 * открытой записи — `<button class="btn-icon"><i class="ph ph-arrow-left"></i></button>`,
 * непонятно куда ведёт до клика). Читается как ссылка, не кнопка — тот же приём, что уже
 * применяется на «Семье» (family-details.component.html, «‹ Семьи»), только вынесен в
 * переиспользуемый компонент, а не инлайн-разметка: та же деталь-страница нужна и аптечке
 * (medkit-detail-page), и мобильному полноэкранному показателю.
 *
 * С `link` рисуется настоящей ссылкой (<a routerLink>: открывается в новой вкладке, читается
 * скринридером как ссылка), без него — кнопкой с событием `activated` (возврат внутри экрана).
 *
 * DESIGN §3: шеврон 18 + текст 15/600 accent, высота 44. `iconOnly` — мобильная шапка: круглая
 * иконочная кнопка, текст уходит в aria-label («Назад: Анализы»).
 */
@Component({
  selector: 'app-back-link',
  standalone: true,
  imports: [RouterLink, IconComponent],
  template: `
    @if (link(); as href) {
      <a class="back-link" [class.back-link-icon]="iconOnly()" [routerLink]="href"
         [attr.aria-label]="iconOnly() ? 'Назад: ' + label() : null">
        <app-icon [icon]="chevron" [size]="iconOnly() ? 22 : 18" />@if (!iconOnly()) {{{ label() }}}
      </a>
    } @else {
      <button type="button" class="back-link" [class.back-link-icon]="iconOnly()" (click)="activated.emit()"
              [attr.aria-label]="iconOnly() ? 'Назад: ' + label() : null">
        <app-icon [icon]="chevron" [size]="iconOnly() ? 22 : 18" />@if (!iconOnly()) {{{ label() }}}
      </button>
    }
  `,
  styleUrl: './back-link.component.scss',
})
export class BackLinkComponent {
  readonly label = input.required<string>();
  readonly link = input<string | readonly unknown[] | null>(null);
  readonly iconOnly = input(false);
  readonly activated = output<void>();

  protected readonly chevron = ChevronLeft;
}
