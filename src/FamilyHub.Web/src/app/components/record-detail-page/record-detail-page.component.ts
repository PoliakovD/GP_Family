import { Component, input } from '@angular/core';
import { MedicalRecordKind } from '../../models/types';
import { MedicalRecordsPanelComponent } from '../medical-records-panel/medical-records-panel.component';

/**
 * Page «Открытая запись» (редизайн v3, PR6) — мобильный экран, на который уводит тап по
 * карточке в списке «Анализы»/«Врачи» (десктоп продолжает раскрывать запись инлайн в списке,
 * см. medical-records-panel.component.ts/isWide). Тонкая обёртка, как medical-records-tab/
 * doctor-visits-tab: вся логика — в панели, здесь только фиксация вида записи и передача :id из
 * роута (withComponentInputBinding уже включён в app.config.ts — id биндится автоматически).
 */
@Component({
  selector: 'app-record-detail-page',
  standalone: true,
  imports: [MedicalRecordsPanelComponent],
  template: `<app-medical-records-panel [kind]="Kind.Analysis" [recordId]="id()" [firstReview]="firstReview()" />`,
})
export class RecordDetailPageComponent {
  readonly id = input.required<string>();
  /** ?firstReview=1 — переход сразу после сохранения в record-add.component.ts (PR7), баннер
   * «Разобрали бланк…» на этот единственный визит. */
  readonly firstReview = input<string | null>(null);
  readonly Kind = MedicalRecordKind;
}
