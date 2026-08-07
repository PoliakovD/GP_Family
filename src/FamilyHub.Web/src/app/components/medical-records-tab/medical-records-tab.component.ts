import { Component } from '@angular/core';
import { MedicalRecordKind } from '../../models/types';
import { MedicalRecordsPanelComponent } from '../medical-records-panel/medical-records-panel.component';

/**
 * Page «Анализы» (вкладка хаба «Здоровье») — тонкая обёртка над MedicalRecordsPanelComponent
 * с зафиксированным видом записи. Вся логика (список/форма/поиск/доступ/вложения) — в панели,
 * переиспользуемой ещё и DoctorVisitsTabComponent («Врачи», тот же хаб).
 */
@Component({
  selector: 'app-medical-records-tab',
  standalone: true,
  imports: [MedicalRecordsPanelComponent],
  template: `<h3 class="mb-3">Анализы</h3>
    <app-medical-records-panel [kind]="Kind.Analysis" />`,
})
export class MedicalRecordsTabComponent {
  readonly Kind = MedicalRecordKind;
}
