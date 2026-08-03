import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { AuthService } from '../../services/auth.service';

/**
 * Гейт согласия ПДн (задача 2.3): текст актуальной версии + чекбокс. Без принятия
 * доступ к медданным закрыт и на сервере (ConsentRequiredFilter).
 */
@Component({
  selector: 'app-consent-gate',
  standalone: true,
  imports: [FormsModule, RouterLink],
  templateUrl: './consent-gate.component.html',
})
export class ConsentGateComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly sanitizer = inject(DomSanitizer);

  readonly consentHtml = signal<SafeHtml | null>(null);
  readonly version = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  agreed = false;
  /** Отдельное обязательное согласие на спецкатегорию — сведения о здоровье (ч. 2 ст. 10
   * 152-ФЗ): по закону нельзя «зашить» его в общий чекбокс, нужен собственный явный отметка. */
  specialCategoryAgreed = false;

  async ngOnInit(): Promise<void> {
    const current = await this.auth.getConsentText();
    this.version.set(current.version);
    // ИНВАРИАНТ: bypassSecurityTrustHtml здесь безопасен ТОЛЬКО пока текст согласия —
    // embedded-ресурс бэкенда (ConsentService.LoadLegalText), не редактируемый пользователем/
    // админкой контент. Если когда-либо появится редактирование этого текста через CMS/админку —
    // этот вызов станет реальной XSS-дырой и должен быть пересмотрен (санитизация через
    // Angular's built-in HTML sanitizer вместо bypass, либо серверная санитизация редактируемого
    // HTML перед отдачей). См. аудит module-review-2026-08-02/08-web-frontend-angular.md, находка 5.
    this.consentHtml.set(this.sanitizer.bypassSecurityTrustHtml(current.text));
  }

  async accept(): Promise<void> {
    if (!this.agreed || !this.specialCategoryAgreed) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.auth.acceptConsent(this.version());
      await this.router.navigate(['/']);
    } catch {
      this.error.set('Не удалось сохранить согласие. Попробуйте ещё раз.');
    } finally {
      this.busy.set(false);
    }
  }
}
