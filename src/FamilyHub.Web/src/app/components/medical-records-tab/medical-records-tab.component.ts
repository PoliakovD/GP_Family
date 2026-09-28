import { Component, input } from '@angular/core';
import { MedicalRecordKind } from '../../models/types';
import { MedicalRecordsPanelComponent } from '../medical-records-panel/medical-records-panel.component';

/**
 * Page «Анализы» (вкладка хаба «Здоровье») — тонкая обёртка над MedicalRecordsPanelComponent
 * с зафиксированным видом записи. Вся логика (список/форма/поиск/доступ/вложения) — в панели,
 * переиспользуемой ещё и DoctorVisitsTabComponent («Посещения врачей», тот же хаб); заголовок
 * экрана тоже печатает панель (редизайн v2.1, вместе со сводкой под ним), не Page-обёртка.
 */
@Component({
    selector: 'app-medical-records-tab',
    imports: [MedicalRecordsPanelComponent],
    template: `<app-medical-records-panel [kind]="Kind.Analysis" [person]="person()" />`
})
export class MedicalRecordsTabComponent {
  readonly Kind = MedicalRecordKind;

  /** ?person=me — плитка хаба «Здоровье» (редизайн навигации) открывает раздел сразу
   * отфильтрованным на себя (см. MedicalRecordsPanelComponent). */
  readonly person = input<string | undefined>(undefined);
}
