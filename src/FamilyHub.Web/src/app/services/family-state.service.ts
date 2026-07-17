import { Injectable, inject, signal, computed } from '@angular/core';
import { ApiService, ApiError } from './api.service';
import { DevLoggerService } from './dev-logger.service';
import { MemberStatus, type FamilySummary } from '../models/types';

@Injectable({ providedIn: 'root' })
export class FamilyStateService {
  private readonly api = inject(ApiService);
  private readonly log = inject(DevLoggerService);

  readonly families = signal<FamilySummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly activeFamilies = computed(() =>
    this.families().filter((f) => f.myStatus === MemberStatus.Active),
  );

  async refresh(): Promise<void> {
    this.log.log('state', 'info', 'refresh()');
    try {
      const result = await this.api.getFamilies();
      this.families.set(result);
      this.error.set(null);
      this.log.log('state', 'info', `families loaded: ${result.length}`);
    } catch (err) {
      const msg = err instanceof ApiError ? err.message : 'Не удалось загрузить семьи.';
      this.error.set(msg);
      this.log.log('state', 'error', `refresh failed: ${msg}`);
    } finally {
      this.loading.set(false);
    }
  }
}
