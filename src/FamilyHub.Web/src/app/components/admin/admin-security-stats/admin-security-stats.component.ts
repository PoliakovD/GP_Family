import { Component, OnInit, inject, signal } from '@angular/core';
import { AdminApiService, AdminSecurityStats } from '../../../services/admin-api.service';

/**
 * «Безопасность → Статистика»: распределение данных по ключам шифрования и сводка безопасности
 * (раньше — нижние карточки страницы «Ключи»). Только чтение. Распределение показывает, сколько
 * значений и вложений ещё лежит на отставном ключе — по нему решают, что перешифровка завершена и
 * отставной ключ можно убрать из конфигурации.
 */
@Component({
  selector: 'app-admin-security-stats',
  standalone: true,
  template: `
    @if (loading()) {
      <p class="text-muted">Загрузка…</p>
    } @else if (error()) {
      <div class="alert-danger">{{ error() }}</div>
    } @else {
      @let s = stats();
      @if (s) {
        <div class="card mb-3">
          <div class="card-title">Распределение по ключам шифрования</div>
          <div class="card-body">
            <p class="mb-1 text-muted">Поля БД:</p>
            <div class="d-flex gap-2 mb-2" style="flex-wrap: wrap;">
              @for (kv of s.encryptionDistribution.fieldValues; track kv.keyId) {
                <span class="tag tag-neutral">{{ kv.keyId }}: {{ kv.count }}</span>
              }
            </div>
            <p class="mb-1 text-muted">Вложения (блобы MinIO):</p>
            <div class="d-flex gap-2" style="flex-wrap: wrap;">
              @for (kv of s.encryptionDistribution.attachmentBlobs; track kv.keyId) {
                <span class="tag tag-neutral">{{ kv.keyId }}: {{ kv.count }}</span>
              }
            </div>
          </div>
        </div>

        <div class="card">
          <div class="card-title">Безопасность</div>
          <div class="card-body">
            <div class="d-flex gap-3" style="flex-wrap: wrap;">
              <div><strong>{{ s.crossUserMedicalAccessLast30Days }}</strong><div class="text-muted">обращений к чужим медданным за 30д</div></div>
              <div><strong>{{ s.usersWithoutCurrentConsent }}</strong><div class="text-muted">без актуального согласия</div></div>
              <div><strong>{{ s.activeSessions }}</strong><div class="text-muted">активных сессий PWA</div></div>
              <div><strong>{{ s.dataProtectionKeyCount }}</strong><div class="text-muted">ключей DataProtection</div></div>
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
