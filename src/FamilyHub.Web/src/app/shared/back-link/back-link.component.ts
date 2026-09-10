import { Component, input, output } from '@angular/core';

/**
 * Редизайн v2.2 — «‹ Название раздела» вместо голой кнопки-иконки без подписи (была на экране
 * открытой записи — `<button class="btn-icon"><i class="ph ph-arrow-left"></i></button>`,
 * непонятно куда ведёт до клика). Читается как ссылка, не кнопка — тот же приём, что уже
 * применяется на «Семье» (family-details.component.html, «‹ Семьи»), только вынесен в
 * переиспользуемый компонент, а не инлайн-разметка: та же деталь-страница нужна и аптечке
 * (medkit-detail-page), и мобильному полноэкранному показателю.
 */
@Component({
  selector: 'app-back-link',
  standalone: true,
  template: `
    <button type="button" class="back-link" (click)="activated.emit()">
      <i class="ph ph-caret-left" aria-hidden="true"></i>{{ label() }}
    </button>
  `,
  styleUrl: './back-link.component.scss',
})
export class BackLinkComponent {
  readonly label = input.required<string>();
  readonly activated = output<void>();
}
