import { Component, input, output } from '@angular/core';
import { MedicalRecordKind } from '../../models/types';
import type { MedicalRecord } from '../../models/types';
import { AvatarComponent } from '../../shared/avatar/avatar.component';
import { BackLinkComponent } from '../../shared/back-link/back-link.component';
import { formatDayMonthYear } from '../../shared/util/date-format';
import { personAvatarPartsFromName, shortenDisplayName, shortenDoctorName } from '../../shared/util/person-name';
import { specimenLabelFor } from './indicator-display';
import { recordPersonKey, recordShortName, unknownIndicatorCount } from './record-display';

/**
 * Шапка экрана одной записи (вынесено из MedicalRecordsPanelComponent): «‹ Анализы» и действия
 * (Файлы/Резюме — переключатели секций, Редактировать, Доступ, Удалить), заголовок, мета-строка
 * (человек · врач · дата · биоматериал · доступ) и плитки «вне нормы / в норме / без нормы».
 */
@Component({
  selector: 'app-record-detail-header',
  standalone: true,
  imports: [AvatarComponent, BackLinkComponent],
  templateUrl: './record-detail-header.component.html',
  styleUrl: './record-detail-header.component.scss',
})
export class RecordDetailHeaderComponent {
  /** null — запись ещё грузится: показываем только «‹ Назад». */
  readonly record = input<MedicalRecord | null | undefined>(null);
  /** Подпись «назад» — название раздела («Анализы» / «Приёмы врача»). */
  readonly backLabel = input.required<string>();
  readonly filesOpen = input(false);
  readonly summaryOpen = input(false);
  /** Есть ли резюме, которое можно развернуть (анализ с показателями). */
  readonly hasSummary = input(false);
  /** Владелец записи — может редактировать и удалять. */
  readonly canEdit = input(false);
  /** Доступ словами («Только вы», «Видит вся семья …»). */
  readonly accessLabel = input('');

  readonly back = output<void>();
  readonly toggleFiles = output<void>();
  readonly toggleSummary = output<void>();
  readonly edit = output<void>();
  readonly access = output<void>();
  readonly remove = output<void>();

  protected readonly Kind = MedicalRecordKind;
  protected readonly formatDayMonthYear = formatDayMonthYear;
  protected readonly shortenDisplayName = shortenDisplayName;
  protected readonly shortenDoctorName = shortenDoctorName;
  protected readonly specimenLabelFor = specimenLabelFor;
  protected readonly shortName = recordShortName;
  protected readonly unknownIndicatorCount = unknownIndicatorCount;

  protected avatarPerson(record: MedicalRecord): { key: string; firstName: string; lastName: string | null } {
    const parts = personAvatarPartsFromName(record.personName);
    return { key: recordPersonKey(record), firstName: parts.firstName, lastName: parts.lastName };
  }
}
