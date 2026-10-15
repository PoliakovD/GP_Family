import { Component, input, output, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';

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
 */
@Component({
  selector: 'app-back-link',
  standalone: true,
  imports: [RouterLink],
  template: `
    @if (link(); as href) {
      <a class="back-link" [routerLink]="href">
        <i class="ph ph-caret-left" aria-hidden="true"></i>{{ label() }}
      </a>
    } @else {
      <button type="button" class="back-link" (click)="activated.emit()">
        <i class="ph ph-caret-left" aria-hidden="true"></i>{{ label() }}
      </button>
    }
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './back-link.component.scss',
})
export class BackLinkComponent {
  readonly label = input.required<string>();
  readonly link = input<string | readonly unknown[] | null>(null);
  readonly activated = output<void>();
}
