import { Component, input } from '@angular/core';
import { MedicalRecordKind } from '../../models/types';
import { MedicalRecordsPanelComponent } from '../medical-records-panel/medical-records-panel.component';

/** Зеркало record-detail-page.component.ts для «Врачи» — см. докстринг там же. */
@Component({
  selector: 'app-doctor-visit-detail-page',
  standalone: true,
  imports: [MedicalRecordsPanelComponent],
  template: `<app-medical-records-panel [kind]="Kind.DoctorVisit" [recordId]="id()" [firstReview]="firstReview()" />`,
})
export class DoctorVisitDetailPageComponent {
  readonly id = input.required<string>();
  readonly firstReview = input<string | null>(null);
  readonly Kind = MedicalRecordKind;
}
