import {Injectable, inject, signal, computed} from '@angular/core';
import {ApiService, ApiError} from './api.service';
import {DevLoggerService} from './dev-logger.service';
import {MemberStatus, type FamilySummary, PendingMember} from '../models/types';

/** Ключ localStorage для запомненного выбора «текущей семьи» (редизайн v2, каркас навигации) —
 * чисто навигационное удобство, не переезд остального приложения на «одна активная семья». */
const SELECTED_FAMILY_STORAGE_KEY = 'familyhub.selectedFamilyId';

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

    /** Текущая выбранная семья (редизайн v2) — пункт «Семья» в сайдбаре/табе ведёт прямо сюда,
     * переключатель семьи в сайдбаре её меняет. Персистится в localStorage, дефолт — первая
     * активная семья. Ни один существующий компонент не обязан на неё переходить — это новый
     * чисто навигационный UI-элемент (см. план редизайна, PR2). */
    private readonly selectedFamilyIdRaw = signal<string | null>(
        typeof localStorage !== 'undefined' ? localStorage.getItem(SELECTED_FAMILY_STORAGE_KEY) : null,
    );

    readonly selectedFamily = computed(() => {
        const active = this.activeFamilies();
        return active.find((f) => f.id === this.selectedFamilyIdRaw()) ?? active[0] ?? null;
    });

    selectFamily(id: string): void {
        this.selectedFamilyIdRaw.set(id);
        try {
            localStorage.setItem(SELECTED_FAMILY_STORAGE_KEY, id);
        } catch {
            // localStorage недоступен (приватный режим и т.п.) — выбор просто не переживёт
            // перезагрузку страницы, не критично для навигационного удобства.
        }
    }

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
                            `lastName:${m.lastName} firstName:${m.firstName}\n` +
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
