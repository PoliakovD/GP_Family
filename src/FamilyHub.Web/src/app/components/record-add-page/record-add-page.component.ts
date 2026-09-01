import { Component } from '@angular/core';
import { MedicalRecordKind } from '../../models/types';
import { RecordAddComponent } from '../record-add/record-add.component';

/** Тонкая обёртка над RecordAddComponent для «Анализы» — зеркало
 * doctor-visit-add.component.ts, тот же паттерн, что medical-records-tab/doctor-visits-tab. */
@Component({
  selector: 'app-record-add-page',
  standalone: true,
  imports: [RecordAddComponent],
  template: `<app-record-add [kind]="Kind.Analysis" />`,
})
export class RecordAddPageComponent {
  readonly Kind = MedicalRecordKind;
}
