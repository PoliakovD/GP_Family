import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { Heart } from 'lucide';
import { IconComponent } from '../icon/icon.component';

/**
 * Экран без оболочки приложения (DESIGN §4 «Экраны без оболочки»): вход, регистрация, привязка
 * Telegram, приглашение, согласие, анкета профиля. Карточка по центру: знак FamilyHub → H1 → пояснение
 * → контент. Без `title` заголовком служит сам контент (согласие: H2 документа с бэкенда). Рядом может стоять блок `[authAside]` (вводный текст на шаге входа): на десктопе слева
 * от карточки, на телефоне — под ней, чтобы форма была первой.
 */
@Component({
  selector: 'app-auth-card',
  standalone: true,
  imports: [IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="auth-shell" [class.auth-shell-wide]="wide()">
      <ng-content select="[authAside]" />
      <section class="card card-lg auth-card" [attr.aria-labelledby]="title() ? headingId : null">
        <div class="auth-brand">
          <span class="app-brand-mark" aria-hidden="true"><app-icon [icon]="heart" [size]="20" [strokeWidth]="2" /></span>
          FamilyHub
        </div>
        @if (title()) { <h1 class="auth-title" [id]="headingId">{{ title() }}</h1> }
        @if (lead(); as l) { <p class="auth-lead">{{ l }}</p> }
        <ng-content />
      </section>
    </div>
  `,
})
export class AuthCardComponent {
  private static nextId = 0;

  readonly title = input<string>('');
  readonly lead = input<string | null>(null);
  /** Длинный текст (согласие): карточка 720px вместо 440px. */
  readonly wide = input(false);

  protected readonly heart = Heart;
  protected readonly headingId = `auth-title-${++AuthCardComponent.nextId}`;
}
