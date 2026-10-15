import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { AdminApiService, AdminSecurityStats } from '../../../services/admin-api.service';
import { LoadingSpinnerComponent } from '../../../shared/loading-spinner/loading-spinner.component';

/**
 * «Безопасность → Статистика»: распределение данных по ключам шифрования и сводка безопасности
 * (раньше — нижние карточки страницы «Ключи»). Только чтение. Распределение показывает, сколько
 * значений и вложений ещё лежит на отставном ключе — по нему решают, что перешифровка завершена и
 * отставной ключ можно убрать из конфигурации.
 */
@Component({
  selector: 'app-admin-security-stats',
  imports: [DatePipe, LoadingSpinnerComponent],
  standalone: true,
  changeDetection: ChangeDetectionStrategy.Eager,
  template: `
    @if (loading()) {
      <app-loading-spinner />
    } @else if (error()) {
      <div class="alert-danger">{{ error() }}</div>
    } @else {
      @let s = stats();
      @if (s) {
        <div class="card mb-3">
          <div class="card-title">Распределение по ключам шифрования</div>
          <div class="card-body">
            <p class="mb-1 text-muted">Поля БД:</p>
            <div class="d-flex gap-2 mb-2 flex-wrap">
              @for (kv of s.encryptionDistribution.fieldValues; track kv.keyId) {
                <span class="badge badge-neutral">{{ kv.keyId }}: {{ kv.count }}</span>
              }
            </div>
            <p class="mb-1 text-muted">Вложения (блобы MinIO):</p>
            <div class="d-flex gap-2 flex-wrap">
              @for (kv of s.encryptionDistribution.attachmentBlobs; track kv.keyId) {
                <span class="badge badge-neutral">{{ kv.keyId }}: {{ kv.count }}</span>
              }
            </div>
          </div>
        </div>

        <div class="card">
          <div class="card-title">Безопасность</div>
          <div class="card-body">
            <div class="stat-grid">
              <div class="stat"><span class="stat-value">{{ s.crossUserMedicalAccessLast30Days }}</span><span class="stat-label">обращений к чужим медданным за 30д</span></div>
              <div class="stat"><span class="stat-value">{{ s.usersWithoutCurrentConsent }}</span><span class="stat-label">без актуального согласия</span></div>
              <div class="stat"><span class="stat-value">{{ s.activeSessions }}</span><span class="stat-label">активных сессий PWA</span></div>
              <div class="stat">
                <span class="stat-value">{{ s.dataProtectionKeyCount }}</span><span class="stat-label">ключей защиты сессий (DataProtection)</span>
                @if (s.oldestDataProtectionKeyCreatedAt) {
                  <span class="stat-label">самый старый — с {{ s.oldestDataProtectionKeyCreatedAt | date: 'dd.MM.yyyy' }}</span>
                }
              </div>
            </div>
          </div>
        </div>
      }
    }
  `,
})
export class AdminSecurityStatsComponent implements OnInit {
  private readonly api = inject(AdminApiService);

  readonly stats = signal<AdminSecurityStats | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.stats.set(await this.api.getSecurityStats());
    } catch {
      this.error.set('Не удалось загрузить статистику безопасности.');
    } finally {
      this.loading.set(false);
    }
  }
}
