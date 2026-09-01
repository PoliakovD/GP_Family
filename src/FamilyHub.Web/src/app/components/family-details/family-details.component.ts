import {Component, Input, OnDestroy, OnInit, effect, inject, signal, WritableSignal} from '@angular/core';
import {ActivatedRoute, Router, RouterLink} from '@angular/router';
import {Subscription} from 'rxjs';
import {ApiService, ApiError} from '../../services/api.service';
import {FamilyStateService} from '../../services/family-state.service';
import {AuthService} from '../../services/auth.service';
import {
    FamilyRole,
    MemberStatus,
    type FamilySummary,
    type InviteCreated,
    type PendingMember, RemoveMemberResult,
} from '../../models/types';
import {MedkitsPanelComponent} from '../medkits-panel/medkits-panel.component';
import {BirthdaysPanelComponent} from '../birthdays-panel/birthdays-panel.component';
import {DependentsPanelComponent} from '../dependents-panel/dependents-panel.component';
import {DatePipe} from "@angular/common";
import {ToastService} from '../../shared/toast/toast.service';
import {ConfirmService} from '../../shared/confirm/confirm.service';
import {ModalComponent} from '../../shared/modal/modal.component';
import {TelegramService} from '../../services/telegram.service';
import {PersonNameComponent} from '../../shared/person-name/person-name.component';
import {AvatarComponent} from '../../shared/avatar/avatar.component';
import {ActionMenuComponent, type ActionMenuItem} from '../../shared/action-menu/action-menu.component';

type FamilySubTab = 'members' | 'medkits' | 'birthdays' | 'dependents';

@Component({
    selector: 'app-family-details',
    standalone: true,
    imports: [
        RouterLink, MedkitsPanelComponent, BirthdaysPanelComponent, DependentsPanelComponent,
        DatePipe, ModalComponent, PersonNameComponent, AvatarComponent, ActionMenuComponent,
    ],
    templateUrl: './family-details.component.html',
    styleUrl: './family-details.component.scss',
})
export class FamilyDetailsComponent implements OnInit, OnDestroy {
    @Input() id!: string;

    readonly state = inject(FamilyStateService);
    readonly auth = inject(AuthService);
    private readonly api = inject(ApiService);
    private readonly toast = inject(ToastService);
    private readonly confirm = inject(ConfirmService);
    private readonly router = inject(Router);
    private readonly route = inject(ActivatedRoute);
    private readonly tg = inject(TelegramService);

    pendingMembers: PendingMember[] | undefined = undefined;
    createdInvite: WritableSignal<InviteCreated> | null = null;
    activeSubTab: FamilySubTab = 'members';
    showInviteModal = false;
    creatingInvite = false;
    /** Семьи, которым Я (владелец записей) открыл(а) все свои анализы — редизайн v2, карточка
     * "это вы" в списке участников. Не путать с доступом ДРУГИХ участников — это видит только
     * сам владелец (тот же принцип, что и в medical-records-panel). */
    myMedicalShares: string[] = [];

    readonly FamilyRole = FamilyRole;
    readonly MemberStatus = MemberStatus;

    readonly subTabs: { id: FamilySubTab; label: string }[] = [
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
            this.activeSubTab = known ? (requested as FamilySubTab) : 'members';
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
    }

    /** Клик по саб-табу — навигация с queryParamsHandling:'merge' вместо прямого присваивания
     * activeSubTab: URL остаётся источником истины (переживает refresh, работает browser back),
     * сам activeSubTab обновится реактивно из подписки выше. */
    selectSubTab(tab: FamilySubTab): void {
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


    async handleApprove(userId: string): Promise<void> {
        try {
            await this.api.approveMember(this.id, userId);
            await this.loadPending();
            await this.state.refresh();
            this.toast.success('Заявка принята.');
        } catch (err) {
            this.toast.error(err instanceof ApiError ? err.message : 'Ошибка при подтверждении.');
        }
    }

    async handleReject(userId: string): Promise<void> {
        try {
            await this.api.rejectMember(this.id, userId);
            await this.loadPending();
            await this.state.refresh();
            this.toast.success('Заявка отклонена.');
        } catch (err) {
            this.toast.error(err instanceof ApiError ? err.message : 'Ошибка при отклонении.');
        }
    }

    openInviteModal(): void {
        this.showInviteModal = true;
    }

    closeInviteModal(): void {
        this.showInviteModal = false;
    }

    async handleCreateInvite(): Promise<void> {
        this.creatingInvite = true;
        try {
            const invite = await this.api.createInvite(this.id);
            if (this.createdInvite) {
                this.createdInvite.set(invite);
            } else {
                this.createdInvite = signal(invite);
            }
            this.toast.success('Инвайт создан.');
        } catch (err) {
            this.toast.error(err instanceof ApiError ? err.message : 'Не удалось создать инвайт.');
        } finally {
            this.creatingInvite = false;
        }
    }

    /** Кнопка-самолётик — открывает бот-диплинк инвайта напрямую, в отличие от shareInvite() ниже
     * (который открывает системный шаринг ссылки, а не саму Telegram-ссылку). */
    openTelegramInvite(telegramLink: string): void {
        this.tg.openTelegramLink(telegramLink);
    }

    async shareInvite(link: string): Promise<void> {
        // Внутри Telegram — открываем нативный шаринг (пользователь сам выбирает контакт/чат
        // из списка Telegram; мы не запрашиваем и не храним чужие Telegram ID).
        if (this.tg.isInsideTelegram()) {
            const shareUrl = `https://t.me/share/url?url=${encodeURIComponent(link)}&text=${encodeURIComponent('Присоединяйтесь к нашей семье в FamilyHub')}`;
            this.tg.openTelegramLink(shareUrl);
            return;
        }

        if (navigator.share) {
            try {
                await navigator.share({
                    title: 'Приглашение в семью FamilyHub',
                    text: 'Присоединяйтесь к нашей семье в FamilyHub',
                    url: link,
                });
            } catch {
                // пользователь отменил диалог — игнорируем
            }
        } else {
            await navigator.clipboard.writeText(link);
            this.toast.success('Ссылка скопирована в буфер обмена.');
        }
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
                    this.toast.success('Пользователь успешно удален из семьи');
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
