import { Injectable, inject, signal, computed } from '@angular/core';
import { ApiService, ApiError } from './api.service';
import { MemberStatus, type FamilySummary } from '../models/types';

@Injectable({ providedIn: 'root' })
export class FamilyStateService {
  private readonly api = inject(ApiService);

  readonly families = signal<FamilySummary[]>([]);
  readonly activeFamilyId = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly activeFamilies = computed(() =>
    this.families().filter((f) => f.myStatus === MemberStatus.Active),
  );

  async refresh(): Promise<void> {
    try {
      const result = await this.api.getFamilies();
      this.families.set(result);
      const current = this.activeFamilyId();
      if (!current || !result.some((f) => f.id === current)) {
        const firstActive = result.find((f) => f.myStatus === MemberStatus.Active);
        this.activeFamilyId.set(firstActive?.id ?? null);
      }
      this.error.set(null);
    } catch (err) {
      this.error.set(err instanceof ApiError ? err.message : 'Не удалось загрузить семьи.');
    } finally {
      this.loading.set(false);
    }
  }

  setActiveFamily(id: string): void {
    this.activeFamilyId.set(id);
  }
}
