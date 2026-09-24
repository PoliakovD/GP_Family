import { Component, OnInit, inject, signal } from '@angular/core';
import { AdminApiService, AdminConfig, ConfigItem, ConfigSource } from '../../../services/admin-api.service';

const SOURCE_LABELS: Record<ConfigSource, { label: string; tag: string }> = {
  env: { label: 'env', tag: 'tag-accent' },
  appsettings: { label: 'appsettings', tag: 'tag-neutral' },
  cli: { label: 'командная строка', tag: 'tag-neutral' },
  other: { label: 'другой источник', tag: 'tag-neutral' },
  default: { label: 'по умолчанию', tag: 'tag-outline' },
};

/**
 * «Настройки → Конфиг env»: что сейчас реально действует и откуда взято. Только чтение — эти
 * настройки живут в env/PROD_ENV и меняются правкой конфигурации сервера с передеплоем, а не из
 * админки (в отличие от остальных страниц раздела «Настройки»).
 *
 * Секреты показываются только как «задан / не задан» — бэкенд значения не отдаёт вообще
 * (AdminConfigService), фронтенду нечего скрывать.
 */
@Component({
  selector: 'app-admin-config-env',
  standalone: true,
  templateUrl: './admin-config-env.component.html',
})
export class AdminConfigEnvComponent implements OnInit {
  private readonly api = inject(AdminApiService);

  readonly config = signal<AdminConfig | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.config.set(await this.api.getConfig());
    } catch {
      this.error.set('Не удалось загрузить конфигурацию.');
    } finally {
      this.loading.set(false);
    }
  }

  /** Имя переменной окружения: `Enrichment:Provider` → `Enrichment__Provider` (как в .env). */
  envName(item: ConfigItem): string {
    return item.key.replace(/:/g, '__');
  }

  sourceLabel(source: ConfigSource): string {
    return SOURCE_LABELS[source]?.label ?? source;
  }

  sourceTag(source: ConfigSource): string {
    return SOURCE_LABELS[source]?.tag ?? 'tag-neutral';
  }
}
