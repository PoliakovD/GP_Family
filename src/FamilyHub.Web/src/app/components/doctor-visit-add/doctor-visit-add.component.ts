import { Component } from '@angular/core';
import { MedicalRecordKind } from '../../models/types';
import { RecordAddComponent } from '../record-add/record-add.component';

/** Зеркало record-add для «Врачи» — тот же тонкий wrapper-паттерн, что
 * medical-records-tab/doctor-visits-tab и record-detail-page/doctor-visit-detail-page. */
@Component({
  selector: 'app-doctor-visit-add',
  standalone: true,
  imports: [RecordAddComponent],
  template: `<app-record-add [kind]="Kind.DoctorVisit" />`,
})
export class DoctorVisitAddComponent {
  readonly Kind = MedicalRecordKind;
}
