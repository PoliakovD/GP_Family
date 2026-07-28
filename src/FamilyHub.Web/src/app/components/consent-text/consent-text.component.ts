import { Component, OnInit, inject, signal } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { AuthService } from '../../services/auth.service';

/**
 * Публичная страница ТЕКСТА согласия на обработку ПДн (без формы принятия) — до этого коммита
 * единственным местом с текстом согласия был /consent, защищённый authGuard (это гейт
 * ПРИНЯТИЯ для уже вошедшего пользователя). Неавторизованный человек, заполняющий форму
 * регистрации и желающий прочитать, на что он соглашается ДО создания аккаунта, на /consent
 * получал редирект на /login — читать было негде. Эта страница — read-only зеркало того же
 * текста (/api/consents/current, уже anonymous на бэкенде), по образцу PrivacyComponent.
 */
@Component({
  selector: 'app-consent-text',
  standalone: true,
  template: `
    <div class="card" style="max-width: 720px; margin: 1.5rem auto; padding: 1.5rem;">
      @if (html(); as content) {
        <div [innerHTML]="content"></div>
      } @else {
        <p>Загрузка…</p>
      }
    </div>
  `,
})
export class ConsentTextComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly sanitizer = inject(DomSanitizer);

  readonly html = signal<SafeHtml | null>(null);

  async ngOnInit(): Promise<void> {
    // Текст согласия — embedded-ресурс нашего бэкенда, доверенный HTML (тот же источник,
    // что и на /consent).
    const current = await this.auth.getConsentText();
    this.html.set(this.sanitizer.bypassSecurityTrustHtml(current.text));
  }
}
