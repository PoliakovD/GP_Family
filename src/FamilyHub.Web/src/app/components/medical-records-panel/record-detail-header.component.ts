import { Component, computed, input, model, output, ChangeDetectionStrategy } from '@angular/core';
import { Eye, FileText, Link, Lock } from 'lucide';
import { MedicalRecordKind } from '../../models/types';
import type { MedicalRecord } from '../../models/types';
import { ActionMenuComponent, type ActionMenuItem } from '../../shared/action-menu/action-menu.component';
import { AvatarComponent } from '../../shared/avatar/avatar.component';
import { BackLinkComponent } from '../../shared/back-link/back-link.component';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { StatFilterComponent } from '../../shared/ui/stat-filter/stat-filter.component';
import { formatDayMonthYear } from '../../shared/util/date-format';
import { personAvatarPartsFromName, shortenDisplayName, shortenDoctorName } from '../../shared/util/person-name';
import { type IndicatorStatusFilter, specimenLabelFor } from './indicator-display';
import { recordPersonKey, recordShortName, unknownIndicatorCount } from './record-display';

/**
 * Шапка экрана одной записи (DESIGN §3 «Анализ»): «‹ Анализы» и действия (Бланк/Файлы и Резюме —
 * переключатели секций, Доступ, «⋯» с Редактировать/Удалить), H1, чипы (человек · врач · дата ·
 * биоматериал · доступ) и счётчики-фильтры «вне нормы / в норме / без нормы».
 */
@Component({
  changeDetection: ChangeDetectionStrategy.Eager,
  selector: 'app-record-detail-header',
  standalone: true,
  imports: [ActionMenuComponent, AvatarComponent, BackLinkComponent, IconComponent, StatFilterComponent],
  templateUrl: './record-detail-header.component.html',
  styleUrl: './record-detail-header.component.scss',
})
export class RecordDetailHeaderComponent {
  /** null — запись ещё грузится: показываем только «‹ Назад». */
  readonly record = input<MedicalRecord | null | undefined>(null);
  /** Подпись «назад» — название раздела («Анализы» / «Приёмы врача»). */
  readonly backLabel = input.required<string>();
  /** Десктоп (≥1024): действия кнопками с подписями; иначе — «Доступ» иконкой, остальное в «⋯». */
  readonly wide = input(true);
  readonly filesOpen = input(false);
  readonly summaryOpen = input(false);
  /** Есть ли резюме, которое можно развернуть (анализ с показателями). */
  readonly hasSummary = input(false);
  /** Владелец записи — может редактировать и удалять. */
  readonly canEdit = input(false);
  /** Доступ словами («Только вы», «Все семьи»). */
  readonly accessLabel = input('');
  /** Нажатый счётчик-фильтр; null — показаны все показатели. */
  readonly statusFilter = model<IndicatorStatusFilter | null>(null);

  readonly back = output<void>();
  readonly toggleFiles = output<void>();
  readonly toggleSummary = output<void>();
  readonly edit = output<void>();
  readonly access = output<void>();
  readonly remove = output<void>();

  protected readonly icons = { Eye, FileText, Link, Lock };
  protected readonly Kind = MedicalRecordKind;
  protected readonly formatDayMonthYear = formatDayMonthYear;
  protected readonly shortenDisplayName = shortenDisplayName;
  protected readonly shortenDoctorName = shortenDoctorName;
  protected readonly specimenLabelFor = specimenLabelFor;
  protected readonly shortName = recordShortName;
  protected readonly unknownIndicatorCount = unknownIndicatorCount;

  /** «Бланк» у анализа (в макете), «Файлы» у приёма врача; число — если файлов несколько. */
  protected readonly filesLabel = computed(() => {
    const r = this.record();
    if (!r) return '';
    const base = r.kind === MedicalRecordKind.Analysis ? 'Бланк' : 'Файлы';
    return r.attachmentCount > 1 ? `${base} (${r.attachmentCount})` : base;
  });

  /** «⋯»: на десктопе — редкие действия; на телефоне туда же уходят Бланк и Резюме. */
  protected readonly menuActions = computed<ActionMenuItem[]>(() => {
    const items: ActionMenuItem[] = [];
    if (!this.wide()) {
      items.push({ label: this.filesOpen() ? 'Скрыть файлы' : this.filesLabel(), handler: () => this.toggleFiles.emit() });
      if (this.hasSummary()) {
        items.push({ label: this.summaryOpen() ? 'Скрыть резюме' : 'Резюме для врача', handler: () => this.toggleSummary.emit() });
      }
    }
    if (this.canEdit()) {
      items.push({ label: 'Редактировать', handler: () => this.edit.emit() });
      items.push({ label: 'Удалить', danger: true, handler: () => this.remove.emit() });
    }
    return items;
  });

  protected setFilter(filter: IndicatorStatusFilter, pressed: boolean): void {
    this.statusFilter.set(pressed ? filter : null);
  }

  protected avatarPerson(record: MedicalRecord): { key: string; firstName: string; lastName: string | null } {
    const parts = personAvatarPartsFromName(record.personName);
    return { key: recordPersonKey(record), firstName: parts.firstName, lastName: parts.lastName };
  }
}
