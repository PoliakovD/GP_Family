import { Component } from '@angular/core';
import { MedicalRecordKind } from '../../models/types';
import { RecordBatchAddComponent } from '../record-batch-add/record-batch-add.component';

/** Зеркало record-batch-add-page для «Врачи» — тот же тонкий wrapper-паттерн, что
 * record-add-page/doctor-visit-add. */
@Component({
  selector: 'app-doctor-visit-batch-add',
  standalone: true,
  imports: [RecordBatchAddComponent],
  template: `<app-record-batch-add [kind]="Kind.DoctorVisit" />`,
})
export class DoctorVisitBatchAddComponent {
  readonly Kind = MedicalRecordKind;
}
