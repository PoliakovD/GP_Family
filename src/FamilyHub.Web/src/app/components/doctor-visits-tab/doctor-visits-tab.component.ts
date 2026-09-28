import { Component, input } from '@angular/core';
import { MedicalRecordKind } from '../../models/types';
import { MedicalRecordsPanelComponent } from '../medical-records-panel/medical-records-panel.component';

/**
 * Page «Посещения врачей» (вкладка хаба «Здоровье») — тонкая обёртка над
 * MedicalRecordsPanelComponent с видом DoctorVisit. Плоский список посещений (не справочник
 * врачей) — см. план разделения. Заголовок экрана печатает панель (редизайн v2.1), не эта обёртка.
 */
@Component({
    selector: 'app-doctor-visits-tab',
    imports: [MedicalRecordsPanelComponent],
    template: `<app-medical-records-panel [kind]="Kind.DoctorVisit" [person]="person()" />`
})
export class DoctorVisitsTabComponent {
  readonly Kind = MedicalRecordKind;

  /** ?person=me — плитка хаба «Здоровье» (редизайн навигации) открывает раздел сразу
   * отфильтрованным на себя (см. MedicalRecordsPanelComponent). */
  readonly person = input<string | undefined>(undefined);
}
