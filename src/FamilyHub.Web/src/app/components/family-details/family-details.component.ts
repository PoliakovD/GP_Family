import {Component, Input, OnInit, inject, signal, WritableSignal} from '@angular/core';
import {RouterLink} from '@angular/router';
import {ApiService, ApiError} from '../../services/api.service';
import {FamilyStateService} from '../../services/family-state.service';
import {
    FamilyRole,
    MemberStatus,
    type FamilySummary,
    type InviteCreated,
    type PendingMember, RemoveMemberResult,
} from '../../models/types';
import {MedkitsPanelComponent} from '../medkits-panel/medkits-panel.component';
import {BirthdaysPanelComponent} from '../birthdays-panel/birthdays-panel.component';
import {DatePipe} from "@angular/common";

type FamilySubTab = 'members' | 'medkits' | 'birthdays';

@Component({
    selector: 'app-family-details',
    standalone: true,
    imports: [RouterLink, MedkitsPanelComponent, BirthdaysPanelComponent, DatePipe],
    templateUrl: './family-details.component.html',
})
export class FamilyDetailsComponent implements OnInit {
    @Input() id!: string;

    readonly state = inject(FamilyStateService);
    private readonly api = inject(ApiService);

    pendingMembers: PendingMember[] | undefined = undefined;
    createdInvite: WritableSignal<InviteCreated> | null = null;
    message: WritableSignal<string> | null = null;
    activeSubTab: FamilySubTab = 'members';

    readonly FamilyRole = FamilyRole;
    readonly MemberStatus = MemberStatus;

    readonly subTabs: { id: FamilySubTab; label: string }[] = [
        {id: 'members', label: 'Участники'},
        {id: 'medkits', label: 'Аптечки'},
        {id: 'birthdays', label: 'Дни рождения'},
    ];

    get family(): FamilySummary | undefined {
        return this.state.families().find((f) => f.id === this.id);
    }

    ngOnInit(): void {
        // Семьи могут ещё не быть загружены при прямом переходе по URL
        if (this.state.families().length === 0) {
            void this.state.refresh();
        }
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
            this.message = null;
        } catch (err) {
            this.message?.set(err instanceof ApiError ? err.message : 'Не удалось загрузить заявки.');
        }
    }


    async handleApprove(userId: string): Promise<void> {
        try {
            await this.api.approveMember(this.id, userId);
            await this.loadPending();

            await this.state.refresh();
        } catch (err) {
            this.message?.set(err instanceof ApiError ? err.message : 'Ошибка при подтверждении.');
        }
    }

    async handleReject(userId: string): Promise<void> {
        try {
            await this.api.rejectMember(this.id, userId);
            await this.loadPending();
            await this.state.refresh();
        } catch (err) {
            this.message?.set(err instanceof ApiError ? err.message : 'Ошибка при отклонении.');
        }
    }

    async handleCreateInvite(): Promise<void> {
        try {
            const invite = await this.api.createInvite(this.id);
            if (this.createdInvite) {
                this.createdInvite.set(invite);
            } else {
                this.createdInvite = signal(invite);
            }
            this.message = null;
        } catch (err) {
            this.message?.set(err instanceof ApiError ? err.message : 'Не удалось создать инвайт.');
        }
    }

    async shareInvite(link: string): Promise<void> {
        const shareData = {
            title: 'Приглашение в семью FamilyHub',
            text: 'Присоединяйтесь к нашей семье в FamilyHub',
            url: link,
        };
        if (navigator.share) {
            try {
                await navigator.share(shareData);
            } catch {
                // пользователь отменил диалог — игнорируем
            }
        } else {
            await navigator.clipboard.writeText(link);
            this.message?.set('Ссылка скопирована в буфер обмена.');
        }
    }

    async removeMember(memberId: string): Promise<void> {
        try {
            const result = await this.api.removeMember(this.id, memberId);
            switch (result) {
                case RemoveMemberResult.Removed:
                    this.message?.set('Пользователь успешно удален из семьи');
                    this.state.refresh();
                    break;

                case RemoveMemberResult.LastAdmin:
                    this.message?.set('Нельзя удалить единственного администратора!');
                    break;

                case RemoveMemberResult.Forbidden:
                    this.message?.set('У вас недостаточно прав для этого действия');
                    break;

                case RemoveMemberResult.NotFound:
                    this.message?.set('Пользователь или семья не найдены');
                    break;
            }
        } catch (err) {
            // Сюда упадут только критические ошибки (типа 500 Server Error или если лег интернет)
            this.message?.set('Произошла непредвиденная ошибка на сервере');
        }
    }

}
