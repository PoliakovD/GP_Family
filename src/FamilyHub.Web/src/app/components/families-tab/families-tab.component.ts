import { Component, computed, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, ApiError } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import { FamilyRole, MemberStatus, MAX_FAMILIES_PER_USER } from '../../models/types';
import { ToastService } from '../../shared/toast/toast.service';
import { ModalComponent } from '../../shared/modal/modal.component';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';

@Component({
  selector: 'app-families-tab',
  standalone: true,
  imports: [FormsModule, RouterLink, ModalComponent, LoadingSpinnerComponent],
  templateUrl: './families-tab.component.html',
})
export class FamiliesTabComponent {
  readonly state = inject(FamilyStateService);
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

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
    if (!this.newFamilyName.trim()) return;
    this.busy = true;
    try {
      await this.api.createFamily(this.newFamilyName.trim());
      this.showCreateModal = false;
      this.toast.success('Семья создана.');
      await this.state.refresh();
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось создать семью.');
    } finally {
      this.busy = false;
    }
  }

  async handleRedeem(): Promise<void> {
    if (!this.inviteCode.trim()) return;
    this.busy = true;
    try {
      const result = await this.api.redeemInvite(this.inviteCode.trim());
      this.toast.success(
        result.status === 'joined'
          ? 'Вы присоединились к семье.'
          : 'Заявка отправлена, ожидайте подтверждения администратором.',
      );
      this.inviteCode = '';
      await this.state.refresh();
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось погасить инвайт.');
    } finally {
      this.busy = false;
    }
  }
}
