import { Component } from '@angular/core';
import { MedicalRecordKind } from '../../models/types';
import { RecordBatchAddComponent } from '../record-batch-add/record-batch-add.component';

/** Тонкая обёртка над RecordBatchAddComponent для «Анализы» — зеркало record-add-page/
 * doctor-visit-add, тот же тонкий wrapper-паттерн (см. class doc RecordBatchAddComponent). */
@Component({
  selector: 'app-record-batch-add-page',
  standalone: true,
  imports: [RecordBatchAddComponent],
  template: `<app-record-batch-add [kind]="Kind.Analysis" />`,
})
export class RecordBatchAddPageComponent {
  readonly Kind = MedicalRecordKind;
}
