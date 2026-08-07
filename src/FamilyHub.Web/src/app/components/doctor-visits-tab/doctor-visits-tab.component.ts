import { Component } from '@angular/core';
import { MedicalRecordKind } from '../../models/types';
import { MedicalRecordsPanelComponent } from '../medical-records-panel/medical-records-panel.component';

/**
 * Page «Врачи» (вкладка хаба «Здоровье») — тонкая обёртка над MedicalRecordsPanelComponent
 * с видом DoctorVisit. Плоский список посещений (не справочник врачей) — см. план разделения.
 */
@Component({
  selector: 'app-doctor-visits-tab',
  standalone: true,
  imports: [MedicalRecordsPanelComponent],
  template: `<h3 class="mb-3">Врачи</h3>
    <app-medical-records-panel [kind]="Kind.DoctorVisit" />`,
})
export class DoctorVisitsTabComponent {
  readonly Kind = MedicalRecordKind;
}
