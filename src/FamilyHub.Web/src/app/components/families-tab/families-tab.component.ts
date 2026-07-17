import { Component, Input, Output, EventEmitter, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import { FamilyRole, MemberStatus, type FamilySummary, type PendingMember } from '../../models/types';

@Component({
  selector: 'app-families-tab',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './families-tab.component.html',
})
export class FamiliesTabComponent {
  @Input() families: FamilySummary[] = [];
  @Output() changed = new EventEmitter<void>();

  private readonly api = inject(ApiService);

  newFamilyName = '';
  inviteCode = '';
  busy = false;
  message: string | null = null;
  pendingByFamily: Record<string, PendingMember[]> = {};
  createdInvite: { familyId: string; code: string } | null = null;

  readonly FamilyRole = FamilyRole;
  readonly MemberStatus = MemberStatus;

  statusLabel(status: number): string {
    return status === MemberStatus.Active ? 'активен' : 'ожидает подтверждения';
  }

  roleLabel(role: number): string {
    return role === FamilyRole.Admin ? 'вы админ' : 'вы участник';
  }

  async handleCreateFamily(): Promise<void> {
    if (!this.newFamilyName.trim()) return;
    this.busy = true;
    try {
      await this.api.createFamily(this.newFamilyName.trim());
      this.newFamilyName = '';
      this.message = 'Семья создана.';
      this.changed.emit();
    } catch (err) {
      this.message = err instanceof ApiError ? err.message : 'Не удалось создать семью.';
    } finally {
      this.busy = false;
    }
  }

  async handleRedeem(): Promise<void> {
    if (!this.inviteCode.trim()) return;
    this.busy = true;
    try {
      const result = await this.api.redeemInvite(this.inviteCode.trim());
      this.message =
        result.status === 'joined'
          ? 'Вы присоединились к семье.'
          : 'Заявка отправлена, ожидайте подтверждения администратором.';
      this.inviteCode = '';
      this.changed.emit();
    } catch (err) {
      this.message = err instanceof ApiError ? err.message : 'Не удалось погасить инвайт.';
    } finally {
      this.busy = false;
    }
  }

  async loadPending(familyId: string): Promise<void> {
    try {
      const pending = await this.api.getPendingMembers(familyId);
      this.pendingByFamily = { ...this.pendingByFamily, [familyId]: pending };
    } catch (err) {
      this.message = err instanceof ApiError ? err.message : 'Не удалось загрузить заявки.';
    }
  }

  async handleApprove(familyId: string, userId: string): Promise<void> {
    await this.api.approveMember(familyId, userId);
    await this.loadPending(familyId);
    this.changed.emit();
  }

  async handleReject(familyId: string, userId: string): Promise<void> {
    await this.api.rejectMember(familyId, userId);
    await this.loadPending(familyId);
    this.changed.emit();
  }

  async handleCreateInvite(familyId: string): Promise<void> {
    try {
      const invite = await this.api.createInvite(familyId);
      this.createdInvite = { familyId, code: invite.code };
    } catch (err) {
      this.message = err instanceof ApiError ? err.message : 'Не удалось создать инвайт.';
    }
  }

  pendingFor(familyId: string): PendingMember[] | undefined {
    return this.pendingByFamily[familyId];
  }
}
