import { BackLinkComponent } from '../../shared/back-link/back-link.component';
import {Component, Input, OnDestroy, OnInit, computed, effect, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import {Location} from '@angular/common';
import {ActivatedRoute, Router, RouterLink} from '@angular/router';
import {Subscription} from 'rxjs';
import {ApiService, ApiError} from '../../services/api.service';
import {FamilyStateService} from '../../services/family-state.service';
import {AuthService} from '../../services/auth.service';
import {
    FamilyRole,
    MemberStatus,
    type FamilySummary,
    type PendingMember, RemoveMemberResult,
} from '../../models/types';
import {MedkitsPanelComponent} from '../medkits-panel/medkits-panel.component';
import {BirthdaysPanelComponent} from '../birthdays-panel/birthdays-panel.component';
import {DependentsPanelComponent} from '../dependents-panel/dependents-panel.component';
import {FamilyChatComponent} from '../family-chat/family-chat.component';
import {ChatRealtimeService} from '../../services/chat-realtime.service';
import {NgTemplateOutlet} from "@angular/common";
import {ToastService} from '../../shared/toast/toast.service';
import {ConfirmService} from '../../shared/confirm/confirm.service';
import {InviteModalComponent} from '../../shared/invite-modal/invite-modal.component';
import {ActionMenuComponent, type ActionMenuItem} from '../../shared/action-menu/action-menu.component';
import {PillTabsComponent, type PillTab} from '../../shared/ui/pill-tabs/pill-tabs.component';
import {PersonRowComponent} from '../../shared/ui/person-row/person-row.component';
import {IconComponent} from '../../shared/ui/icon/icon.component';
import {BreakpointService} from '../../services/breakpoint.service';
import {PageActionService} from '../../services/page-action.service';
import {pluralizeRu} from '../../shared/util/pluralize';
import {formatPersonName} from '../../shared/util/person-name';
import {Check, Link, Lock} from 'lucide';

type FamilySubTab = 'members' | 'chat' | 'medkits' | 'birthdays' | 'dependents';

@Component({
    changeDetection: ChangeDetectionStrategy.Eager,
    selector: 'app-family-details',
    imports: [
        RouterLink, MedkitsPanelComponent, BirthdaysPanelComponent, DependentsPanelComponent, FamilyChatComponent,
        InviteModalComponent, ActionMenuComponent, BackLinkComponent,
        PillTabsComponent, PersonRowComponent, IconComponent, NgTemplateOutlet,
    ],
    templateUrl: './family-details.component.html',
    styleUrl: './family-details.component.scss'
})
export class FamilyDetailsComponent implements OnInit, OnDestroy {
    @Input() id!: string;

    readonly state = inject(FamilyStateService);
    readonly auth = inject(AuthService);
    readonly chat = inject(ChatRealtimeService);
    private readonly api = inject(ApiService);
    private readonly toast = inject(ToastService);
    private readonly confirm = inject(ConfirmService);
    private readonly router = inject(Router);
    private readonly route = inject(ActivatedRoute);
    private readonly location = inject(Location);
    private readonly breakpoints = inject(BreakpointService);
    private readonly pageAction = inject(PageActionService);

    protected readonly icons = {Check, Link, Lock};
    readonly isWide = computed(() => this.breakpoints.tier() === 'wide');
    /** Текущая вкладка — сигнал: от неё зависит полноэкранный режим чата на мобайле. */
    private readonly activeTab = signal<FamilySubTab>('chat');

    pendingMembers: PendingMember[] | undefined = undefined;
    get activeSubTab(): FamilySubTab {
        return this.activeTab();
    }
    showInviteModal = false;
    /** Семьи, которым Я (владелец записей) открыл(а) все свои анализы — редизайн v2, карточка
     * "это вы" в списке участников. Не путать с доступом ДРУГИХ участников — это видит только
     * сам владелец (тот же принцип, что и в medical-records-panel). */
    myMedicalShares: string[] = [];

    readonly FamilyRole = FamilyRole;
    readonly MemberStatus = MemberStatus;

    /** DESIGN §3 «Чат семьи»: чат — первая вкладка раздела. */
    readonly subTabs: { id: FamilySubTab; label: string }[] = [
        {id: 'chat', label: 'Чат'},
        {id: 'members', label: 'Участники'},
        {id: 'medkits', label: 'Аптечки'},
        {id: 'birthdays', label: 'Дни рождения'},
        {id: 'dependents', label: 'Близкие и питомцы'},
    ];

    private paramsSub?: Subscription;
    private pendingAutoLoaded = false;

    get family(): FamilySummary | undefined {
        return this.state.families().find((f) => f.id === this.id);
    }

    constructor() {
        // Чат на телефоне — полноэкранный: без шапки каркаса и таб-бара, со своей шапкой «назад».
        effect(() => {
            const f = this.state.families().find((x) => x.id === this.id);
            this.pageAction.setFullscreen(
                this.activeTab() === 'chat' && !this.isWide() && !!f && f.myStatus === MemberStatus.Active,
            );
        });

        // Редизайн v2 — заявки видны сразу над списком участников, без клика по кнопке
        // «Заявки». state.families() может ещё не быть загружен на момент ngOnInit (refresh()
        // асинхронный, см. FamilyStateService) — реагируем на сигнал напрямую, тем же приёмом,
        // что и остальные Panel-компоненты проекта (load-on-input-change через effect() в
        // конструкторе, см. .claude/patterns/frontend_web.md). Флаг — чтобы не дёргать
        // /pending повторно на каждое обновление families() (approve/reject сами обновляют
        // state.refresh(), это не должно повторно вызывать loadPending — она уже вызывается
        // явно в handleApprove/handleReject).
        effect(() => {
            if (this.pendingAutoLoaded) return;
            const f = this.family;
            if (!f) return; // ещё не загружено — подождём следующего срабатывания
            this.pendingAutoLoaded = true;
            if (f.myRole === FamilyRole.Admin && f.myStatus === MemberStatus.Active) {
                void this.loadPending();
            }
        });
    }

    ngOnInit(): void {
        // Семьи могут ещё не быть загружены при прямом переходе по URL
        if (this.state.families().length === 0) {
            void this.state.refresh();
        }

        // Редизайн v2 — подпункты «Семья» в сайдбаре/навигации ведут сюда через ?tab=, а не на
        // отдельные роуты (FamilyDetailsComponent исторически держит саб-табы in-page, см.
        // .claude/patterns/frontend_web.md «Хаб-паттерн» — осознанное исключение). URL и
        // in-page-состояние синхронизированы в обе стороны: неизвестное/отсутствующее значение
        // молча схлопывается к дефолтной вкладке 'members', а не падает и не показывает пусто.
        this.paramsSub = this.route.queryParamMap.subscribe((params) => {
            const requested = params.get('tab');
            const known = this.subTabs.some((t) => t.id === requested);
            // Без ?tab= — чат (первая вкладка), а пока заявка не подтверждена — участники (чат закрыт).
            const fallback: FamilySubTab = this.family && this.family.myStatus !== MemberStatus.Active ? 'members' : 'chat';
            this.activeTab.set(known ? (requested as FamilySubTab) : fallback);
        });

        void this.loadMyShares();
    }

    private async loadMyShares(): Promise<void> {
        try {
            this.myMedicalShares = await this.api.getMedicalRecordShares();
        } catch {
            // Не критично для этой страницы — карточка "это вы" просто не покажет строку доступа.
        }
    }

    /** Бейдж вкладки «Чат» — непрочитанное этой семьи (ChatRealtimeService). */
    chatUnread(): number {
        return this.chat.unreadByFamily()[this.id] ?? 0;
    }

    hasSharedWithThisFamily(): boolean {
        return this.myMedicalShares.includes(this.id);
    }

    get deleteFamilyMenuActions(): ActionMenuItem[] {
        return [
            {label: 'Удалить семью', icon: 'ph ph-trash', danger: true, handler: () => void this.handleDeleteFamily()},
        ];
    }

    ngOnDestroy(): void {
        this.paramsSub?.unsubscribe();
        this.pageAction.setFullscreen(false);
    }

    /** Вкладки-пилюли (DESIGN §3 «tabs-pill»): у чата — счётчик непрочитанного. */
    get pillTabs(): PillTab[] {
        const unread = this.chatUnread();
        return this.subTabs.map((t) => ({id: t.id, label: t.label, count: t.id === 'chat' ? unread : null}));
    }

    /** «2 участника · вы админ» под названием семьи. */
    get familySubtitle(): string {
        const f = this.family;
        if (!f) return '';
        const n = f.currentMembers?.length ?? 0;
        const role = f.myStatus !== MemberStatus.Active ? 'заявка на рассмотрении'
            : f.myRole === FamilyRole.Admin ? 'вы админ' : 'вы участник';
        return `${n} ${pluralizeRu(n, 'участник', 'участника', 'участников')} · ${role}`;
    }

    get isAdmin(): boolean {
        const f = this.family;
        return !!f && f.myRole === FamilyRole.Admin && f.myStatus === MemberStatus.Active;
    }

    familyInitial(name: string): string {
        return name.trim().charAt(0).toUpperCase() || '?';
    }

    memberName(m: {firstName: string | null; lastName: string | null; middleName: string | null}): string {
        return `${m.firstName ?? ''} ${m.lastName ?? ''}`.trim() || formatPersonName(m, 'full');
    }

    /** «@valeria · с 13 сентября» / «Вы · админ · с 25 августа» (макет family-chat-desktop). */
    memberMeta(m: {id: string; role: number; username: string | null; joinedAt: string}): string {
        const since = `с ${new Date(m.joinedAt).toLocaleDateString('ru-RU', {day: 'numeric', month: 'long'})}`;
        if (m.id === this.auth.me()?.userId) {
            return [m.role === FamilyRole.Admin ? 'Вы · админ' : 'Вы', since].join(' · ');
        }
        return [m.username ? `@${m.username}` : (m.role === FamilyRole.Admin ? 'Админ' : 'Участник'), since].join(' · ');
    }

    private readonly memberActionsCache = new Map<string, ActionMenuItem[]>();

    /** «⋯» у участника: «Исключить» — только админу и не на своей строке. */
    memberActions(memberId: string): ActionMenuItem[] {
        let actions = this.memberActionsCache.get(memberId);
        if (!actions) {
            actions = [{label: 'Исключить из семьи', icon: 'ph ph-user-minus', danger: true, handler: () => void this.removeMember(memberId)}];
            this.memberActionsCache.set(memberId, actions);
        }
        return actions;
    }

    /** «Назад» из полноэкранного чата — туда, откуда пришли; без истории — на главную. */
    goBack(): void {
        if (window.history.length > 1) this.location.back();
        else void this.router.navigate(['/home']);
    }

    /** Клик по саб-табу — навигация с queryParamsHandling:'merge' вместо прямого присваивания
     * activeSubTab: URL остаётся источником истины (переживает refresh, работает browser back),
     * сам activeSubTab обновится реактивно из подписки выше. */
    selectSubTab(tab: FamilySubTab | string): void {
        void this.router.navigate([], {
            relativeTo: this.route,
            queryParams: {tab},
            queryParamsHandling: 'merge',
            replaceUrl: true,
        });
    }

    statusLabel(status: number): string {
        return status === MemberStatus.Active ? 'активен' : 'ожидает подтверждения';
    }

    roleLabel(role: number): string {
        return role === FamilyRole.Admin ? 'админ' : 'участник';
    }

    async loadPending(): Promise<void> {
        try {
            this.pendingMembers = await this.api.getPendingMembers(this.id);
        } catch (err) {
            this.toast.error(err instanceof ApiError ? err.message : 'Не удалось загрузить заявки.');
        }
    }


    /** Заявки, по которым уже идёт запрос — двойной тап слал два запроса, второй падал с ошибкой. */
    readonly busyRequests = new Set<string>();

    async handleApprove(userId: string): Promise<void> {
        if (this.busyRequests.has(userId)) return;
        this.busyRequests.add(userId);
        try {
            await this.api.approveMember(this.id, userId);
            await this.loadPending();
            await this.state.refresh();
            this.toast.success('Заявка принята.');
        } catch (err) {
            this.toast.error(err instanceof ApiError ? err.message : 'Ошибка при подтверждении.');
        } finally {
            this.busyRequests.delete(userId);
        }
    }

    async handleReject(userId: string): Promise<void> {
        if (this.busyRequests.has(userId)) return;
        this.busyRequests.add(userId);
        try {
            await this.api.rejectMember(this.id, userId);
            await this.loadPending();
            await this.state.refresh();
            this.toast.success('Заявка отклонена.');
        } catch (err) {
            this.toast.error(err instanceof ApiError ? err.message : 'Ошибка при отклонении.');
        } finally {
            this.busyRequests.delete(userId);
        }
    }

    openInviteModal(): void {
        this.showInviteModal = true;
    }

    closeInviteModal(): void {
        this.showInviteModal = false;
    }

    async removeMember(memberId: string): Promise<void> {
        const confirmed = await this.confirm.confirm({
            title: 'Выгнать участника?',
            message: 'Участник потеряет доступ к семье и её данным.',
            confirmText: 'Выгнать',
            danger: true,
        });
        if (!confirmed) return;

        try {
            const result = await this.api.removeMember(this.id, memberId);
            switch (result) {
                case RemoveMemberResult.Removed:
                    this.toast.success('Участник исключён из семьи.');
                    this.state.refresh();
                    break;

                case RemoveMemberResult.LastAdmin:
                    this.toast.error('Нельзя удалить единственного администратора!');
                    break;

                case RemoveMemberResult.Forbidden:
                    this.toast.error('У вас недостаточно прав для этого действия');
                    break;

                case RemoveMemberResult.NotFound:
                    this.toast.error('Пользователь или семья не найдены');
                    break;
            }
        } catch (err) {
            // Сюда упадут только критические ошибки (типа 500 Server Error или если лег интернет)
            this.toast.error('Произошла непредвиденная ошибка на сервере');
        }
    }

    async handleDeleteFamily(): Promise<void> {
        const confirmed = await this.confirm.confirm({
            title: 'Удалить семью?',
            message: 'Семья и все её данные (участники, аптечки, дни рождения, инвайты) будут удалены безвозвратно.',
            confirmText: 'Удалить',
            danger: true,
        });
        if (!confirmed) return;

        try {
            await this.api.deleteFamily(this.id);
            this.toast.success('Семья удалена.');
            await this.state.refresh();
            await this.router.navigate(['/families']);
        } catch (err) {
            this.toast.error(err instanceof ApiError ? err.message : 'Не удалось удалить семью.');
        }
    }

}
