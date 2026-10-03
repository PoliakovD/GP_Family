import { Component, inject, input, output } from '@angular/core';
import type { MedicalRecord } from '../../models/types';
import { ApiError, ApiService } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { ToastService } from '../../shared/toast/toast.service';
import { formatDayMonthYear } from '../../shared/util/date-format';
import { isOnlyMe, isVisibleToFamily } from './record-access';

/**
 * Шторка «Настройки доступа» одной медзаписи (вынесена из MedicalRecordsPanelComponent).
 * Состоянием записи и списка шаринга владеет родитель: после любого изменения шторка шлёт
 * `changed`, родитель тихо перечитывает данные и передаёт обновлённые `record`/`shares`.
 */
@Component({
  selector: 'app-record-access-sheet',
  standalone: true,
  imports: [BottomSheetComponent],
  templateUrl: './record-access-sheet.component.html',
})
export class RecordAccessSheetComponent {
  readonly record = input.required<MedicalRecord>();
  /** Семьи с общим шарингом (L1). */
  readonly shares = input.required<readonly string[]>();
  readonly closed = output<void>();
  /** Доступ изменён на сервере — родитель перечитывает записи. */
  readonly changed = output<void>();

  protected readonly state = inject(FamilyStateService);
  private readonly api = inject(ApiService);
  private readonly confirm = inject(ConfirmService);
  private readonly toast = inject(ToastService);

  protected readonly formatDayMonthYear = formatDayMonthYear;

  protected isVisibleToFamily(familyId: string): boolean {
    return isVisibleToFamily(this.record(), this.shares(), familyId);
  }

  protected isOnlyMe(): boolean {
    return isOnlyMe(this.record(), this.shares());
  }

  /**
   * Тумблер одной семьи. Включение автоматически создаёт L1-шаринг, если его ещё не было — иначе
   * тумблер не мог бы включить видимость семье, которой владелец никогда явно не открывал записи.
   * L1-шаринг общий на оба вида («Анализы + Врачи»), поэтому затрагивает видимость всех записей той
   * же семье — осознанно, и поэтому первое включение спрашивает подтверждение.
   */
  protected async setFamilyAccess(familyId: string, visible: boolean, input?: HTMLInputElement): Promise<void> {
    const record = this.record();
    const shares = this.shares();
    if (visible && !shares.includes(familyId)) {
      const name = this.state.families().find((f) => f.id === familyId)?.name ?? 'эта семья';
      const ok = await this.confirm.confirm({
        title: 'Открыть доступ семье?',
        message: `Семья «${name}» увидит все ваши анализы и приёмы врача, кроме тех, что вы скроете от неё отдельно.`,
        confirmText: 'Открыть доступ',
      });
      if (!ok) {
        if (input) input.checked = false;
        return;
      }
    }
    try {
      if (visible) {
        if (!shares.includes(familyId)) await this.api.shareMedicalRecord(familyId);
        await this.api.unhideMedicalRecord(record.id, [familyId]);
      } else {
        await this.api.hideMedicalRecord(record.id, [familyId]);
      }
      this.changed.emit();
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Действие доступно только владельцу записи.');
    }
  }

  /** Сегмент «Только я / Все семьи» — скрытие/раскрытие записи для ВСЕХ уже расшаренных семей. */
  protected async setAccessMode(onlyMe: boolean, input?: HTMLInputElement): Promise<void> {
    const record = this.record();
    const shares = [...this.shares()];
    if (shares.length === 0) {
      // «Все семьи» без единой открытой семьи не должно выглядеть включённым.
      if (input) input.checked = false;
      this.toast.info('Пока ни одной семье доступ не открыт — включите нужную семью ниже.');
      return;
    }
    try {
      if (onlyMe) {
        await this.api.hideMedicalRecord(record.id, shares);
      } else {
        await this.api.unhideMedicalRecord(record.id, shares);
      }
      this.changed.emit();
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Действие доступно только владельцу записи.');
    }
  }
}
