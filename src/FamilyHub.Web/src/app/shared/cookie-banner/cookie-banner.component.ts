import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { CookieConsentService } from './cookie-consent.service';

/**
 * Информационный cookie-баннер (полный Принять/Отклонить). Показывается только в PWA-режиме
 * (Telegram Mini App не хранит выбор в cookie — сессия там неявная через initData, баннер
 * там не нужен и не показывается). Единственный cookie приложения — строго необходимая
 * сессия входа (familyhub.auth), трекинга/аналитики нет.
 */
@Component({
  selector: 'app-cookie-banner',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './cookie-banner.component.html',
})
export class CookieBannerComponent {
  private readonly auth = inject(AuthService);
  private readonly consent = inject(CookieConsentService);

  readonly visible = computed(() => this.auth.mode === 'pwa' && this.consent.choice() === null);

  accept(): void {
    this.consent.setChoice('accepted');
  }

  decline(): void {
    this.consent.setChoice('declined');
  }
}
