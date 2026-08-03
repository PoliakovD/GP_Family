import { Component, OnInit, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { firstValueFrom } from 'rxjs';

/** Публичная страница политики конфиденциальности (задача 2.3). */
@Component({
  selector: 'app-privacy',
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
export class PrivacyComponent implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly sanitizer = inject(DomSanitizer);

  readonly html = signal<SafeHtml | null>(null);

  async ngOnInit(): Promise<void> {
    // ИНВАРИАНТ: bypassSecurityTrustHtml здесь безопасен ТОЛЬКО пока текст политики —
    // embedded-ресурс бэкенда, не редактируемый через CMS/админку контент. См. подробности
    // инварианта в ConsentGateComponent / аудит
    // module-review-2026-08-02/08-web-frontend-angular.md, находка 5.
    const raw = await firstValueFrom(this.http.get('/api/legal/privacy-policy', { responseType: 'text' }));
    this.html.set(this.sanitizer.bypassSecurityTrustHtml(raw));
  }
}
