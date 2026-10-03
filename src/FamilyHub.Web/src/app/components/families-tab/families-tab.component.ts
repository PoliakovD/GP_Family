import { Component, computed, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ApiService, ApiError } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import { FamilyRole, MemberStatus, MAX_FAMILIES_PER_USER } from '../../models/types';
import { ToastService } from '../../shared/toast/toast.service';
import { ModalComponent } from '../../shared/modal/modal.component';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';

/** Людям приходит ссылка вида https://…/join/CODE, а не голый код — принимаем и то и другое. */
export function extractInviteCode(raw: string): string {
  const text = raw.trim();
  const match = text.match(/\/join\/([^/?#\s]+)/);
  return match ? decodeURIComponent(match[1]) : text;
}

@Component({
    selector: 'app-families-tab',
    imports: [FormsModule, RouterLink, ModalComponent, LoadingSpinnerComponent],
    templateUrl: './families-tab.component.html'
})
export class FamiliesTabComponent {
  readonly state = inject(FamilyStateService);
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  constructor() {
    // /families?create=1 — ссылка «Создать семью» с Главной сразу открывает форму.
    if (inject(ActivatedRoute).snapshot.queryParamMap.get('create') === '1') this.openCreateModal();
  }

  newFamilyName = '';
  inviteCode = '';
  busy = false;
  showCreateModal = false;

  readonly FamilyRole = FamilyRole;
  readonly MemberStatus = MemberStatus;
  readonly MAX_FAMILIES_PER_USER = MAX_FAMILIES_PER_USER;

  /** Семьи, где пользователь Admin, == семьи, которые он создал (промоушена в Admin в продукте
   * нет — см. FamilyService.MaxFamiliesPerUser). Гейтит кнопку «Создать» ДО запроса на сервер. */
  readonly createdFamiliesCount = computed(
    () => this.state.families().filter((f) => f.myRole === FamilyRole.Admin).length,
  );

  readonly atFamilyLimit = computed(() => this.createdFamiliesCount() >= MAX_FAMILIES_PER_USER);

  statusLabel(status: number): string {
    return status === MemberStatus.Active ? 'активен' : 'ожидает подтверждения';
  }

  roleLabel(role: number): string {
    return role === FamilyRole.Admin ? 'вы админ' : 'вы участник';
  }

  openCreateModal(): void {
    if (this.atFamilyLimit()) {
      this.toast.error(`Достигнут лимит в ${MAX_FAMILIES_PER_USER} созданных семей.`);
      return;
    }
    this.newFamilyName = '';
    this.showCreateModal = true;
  }

  closeCreateModal(): void {
    this.showCreateModal = false;
  }

  async handleCreateFamily(): Promise<void> {
    // busy-проверка нужна и здесь: Enter в поле не смотрит на [disabled] кнопки, и двойной Enter
    // создавал две семьи.
    if (this.busy) return;
    if (!this.newFamilyName.trim()) {
      this.toast.error('Введите название семьи.');
      return;
    }
    this.busy = true;
    try {
      const { id } = await this.api.createFamily(this.newFamilyName.trim());
      this.showCreateModal = false;
      await this.state.refresh();
      this.state.selectFamily(id);
      this.toast.success('Семья создана. Теперь пригласите близких по ссылке.');
      void this.router.navigate(['/families', id]);
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось создать семью.');
    } finally {
      this.busy = false;
    }
  }

  async handleRedeem(): Promise<void> {
    if (this.busy) return;
    const code = extractInviteCode(this.inviteCode);
    if (!code) {
      this.toast.error('Вставьте ссылку-приглашение или код из неё.');
      return;
    }
    this.busy = true;
    try {
      const result = await this.api.redeemInvite(code);
      this.toast.success(
        result.status === 'joined'
          ? 'Вы присоединились к семье.'
          : 'Заявка отправлена, ожидайте подтверждения администратором.',
      );
      this.inviteCode = '';
      await this.state.refresh();
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось присоединиться по приглашению.');
    } finally {
      this.busy = false;
    }
  }
}
