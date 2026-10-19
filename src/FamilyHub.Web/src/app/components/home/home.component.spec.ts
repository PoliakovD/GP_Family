import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { MedicalRecord, VaccinationOverview } from '../../models/types';
import { ApiService } from '../../services/api.service';
import { AuthService } from '../../services/auth.service';
import { ChatRealtimeService } from '../../services/chat-realtime.service';
import { FamilyStateService } from '../../services/family-state.service';
import { ToastService } from '../../shared/toast/toast.service';
import { HomeComponent } from './home.component';

const record = (id: string, extra: Partial<MedicalRecord>) => ({
  id, ownerUserId: 'me', kind: 0, personName: 'Поляков Даниил Сергеевич', recordDate: '2026-10-03', title: 'ОАК',
  familyDependentId: null, targetUserId: null, abnormalIndicatorCount: 0, ...extra,
}) as unknown as MedicalRecord;

const summary = {
  greetingName: 'Даниил', today: '2026-10-14', attentionTotal: 0, primaryFamilyId: null, primaryFamilyName: null,
  medications: [], joinRequests: [], birthdays: [], ok: { medicationsTotal: 3, pushEnabled: true }, unreadNotifications: 0,
};
const health = {
  analyses: { latest: { id: 'r-me', title: 'ОАК', recordDate: '2026-10-03', abnormalIndicatorCount: 6 }, total: 1 },
  vaccinations: { nextDue: { seriesName: 'Грипп', windowTo: '2026-11-30' }, lastDone: null },
};

/** «Требует внимания»: строки про близких подписаны их именем, свои — «Вы», но только если близкие есть. */
describe('HomeComponent — подписи членов семьи в «Требует внимания»', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    api = {
      getHomeSummary: vi.fn().mockResolvedValue(summary),
      getHealthSummary: vi.fn().mockResolvedValue(health),
      getMedicalRecords: vi.fn().mockResolvedValue({ items: [] }),
      getVaccinationsOverview: vi.fn().mockResolvedValue({ attention: [], people: [] }),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: ApiService, useValue: api },
        { provide: ToastService, useValue: { error: vi.fn() } },
        { provide: AuthService, useValue: { me: signal({ userId: 'me' }) } },
        { provide: FamilyStateService, useValue: { selectedFamily: signal(null) } },
        { provide: ChatRealtimeService, useValue: {} },
      ],
    });
  });

  async function tasks() {
    const home = TestBed.runInInjectionContext(() => new HomeComponent());
    await home.refresh();
    return home.tasks.map((t) => t.subtitle);
  }

  it('без близких — без подписей, как раньше', async () => {
    api['getMedicalRecords'].mockResolvedValue({ items: [record('r-me', { abnormalIndicatorCount: 6 })] });
    expect(await tasks()).toEqual(['ОАК · 3 октября', 'Рекомендуется до 30 ноября 2026']);
  });

  it('анализ близкого и прививка подопечного — с именем; мои строки — «Вы»; по одному последнему анализу на человека', async () => {
    api['getMedicalRecords'].mockResolvedValue({ items: [
      record('r-me', { abnormalIndicatorCount: 6 }),
      record('r-val-old', { ownerUserId: 'val', personName: 'Полякова Валерия Сергеевна', recordDate: '2026-09-01', abnormalIndicatorCount: 5 }),
      record('r-val', { ownerUserId: 'val', personName: 'Полякова Валерия Сергеевна', recordDate: '2026-10-08', abnormalIndicatorCount: 2, title: 'Биохимия' }),
      record('r-dep', { familyDependentId: 'dep', personName: 'Мама Дани', abnormalIndicatorCount: 0 }),
    ] });
    api['getVaccinationsOverview'].mockResolvedValue({
      attention: [
        { subject: { kind: 'user', id: 'me', name: 'Я', isSelf: true, canEdit: true }, item: { seriesName: 'Грипп', windowTo: '2026-11-30' } },
        { subject: { kind: 'dependent', id: 'dep', name: 'Маша', isSelf: false, canEdit: true }, item: { seriesName: 'Корь', windowTo: null } },
        { subject: { kind: 'dependent', id: 'dep', name: 'Маша', isSelf: false, canEdit: true }, item: { seriesName: 'Полиомиелит', windowTo: null } },
      ],
      people: [],
    } as unknown as VaccinationOverview);

    expect(await tasks()).toEqual([
      'Вы · ОАК · 3 октября',
      'Валерия · Биохимия · 8 октября',
      'Вы · Рекомендуется до 30 ноября 2026',
      'Маша · Рекомендуется сейчас',
    ]);
  });

  it('сбой запросов про близких не ломает Главную — остаются мои строки', async () => {
    api['getMedicalRecords'].mockRejectedValue(new Error('500'));
    api['getVaccinationsOverview'].mockRejectedValue(new Error('500'));
    expect(await tasks()).toEqual(['ОАК · 3 октября', 'Рекомендуется до 30 ноября 2026']);
  });
});
