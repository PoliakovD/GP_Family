import {Injectable, inject, signal, computed} from '@angular/core';
import {ApiService, ApiError} from './api.service';
import {DevLoggerService} from './dev-logger.service';
import {MemberStatus, type FamilySummary, PendingMember} from '../models/types';

@Injectable({providedIn: 'root'})
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
            for (const f of this.families()) {
                // Участников видит только активный member (бэкенд проверяет членство —
                // GET /api/families/{id}/current теперь 403 для не-Active статусов, включая
                // собственный PendingApproval). Пропускаем такие семьи и не даём сбою по одной
                // семье оборвать загрузку остальных.
                if (f.myStatus !== MemberStatus.Active) continue;

                try {
                    f.currentMembers = await this.api.getCurrentMembers(f.id);
                    f.currentMembers.forEach((m) => {
                        this.log.log('state', 'info',
                            `Id:${m.id}\n` +
                            `displayName:${m.displayName}\n` +
                            `username:$${m.username}\n` +
                            `role:${m.role}\n` +
                            `joinedAt:${m.joinedAt}`
                        );
                    })
                    this.log.log('state', 'info', 'loaded ' + f.currentMembers.length + 'members');
                } catch (err) {
                    const msg = err instanceof ApiError ? err.message : String(err);
                    this.log.log('state', 'error', `getCurrentMembers(${f.id}) failed: ${msg}`);
                }

                // Подопечные — тот же per-family приём, что currentMembers выше: нужны и панели
                // "Близкие и питомцы", и дропдауну "Кто пациент?" в медзаписях.
                try {
                    f.dependents = await this.api.getDependents(f.id);
                } catch (err) {
                    const msg = err instanceof ApiError ? err.message : String(err);
                    this.log.log('state', 'error', `getDependents(${f.id}) failed: ${msg}`);
                }
            }
        } catch (err) {
            const msg = err instanceof ApiError ? err.message : 'Не удалось загрузить семьи.';
            this.error.set(msg);
            this.log.log('state', 'error', `refresh failed: ${msg}`);
        } finally {
            this.loading.set(false);
        }
    }
}
