import { Component, OnInit, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { AuthService } from '../../services/auth.service';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';

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
  imports: [LoadingSpinnerComponent],
  changeDetection: ChangeDetectionStrategy.Eager,
  template: `
    <article class="card card-lg doc-page">
      @if (html(); as content) {
        <div class="prose" [innerHTML]="content"></div>
      } @else {
        <app-loading-spinner />
      }
    </article>
  `,
})
export class ConsentTextComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly sanitizer = inject(DomSanitizer);

  readonly html = signal<SafeHtml | null>(null);

  async ngOnInit(): Promise<void> {
    // ИНВАРИАНТ: bypassSecurityTrustHtml здесь безопасен ТОЛЬКО пока текст согласия —
    // embedded-ресурс бэкенда (тот же источник, что и на /consent), не редактируемый через
    // CMS/админку контент. См. подробности инварианта в ConsentGateComponent / аудит
    // module-review-2026-08-02/08-web-frontend-angular.md, находка 5.
    const current = await this.auth.getConsentText();
    this.html.set(this.sanitizer.bypassSecurityTrustHtml(current.text));
  }
}
