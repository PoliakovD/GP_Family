import { Component, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import type { KbMedicationCard } from '../../models/types';

/**
 * Карточка препарата из общего справочника (этап 4) — Panel (таксономия из
 * patterns/frontend_web.md): нет своего URL, переиспользуется и в разделе «Справочник»
 * (kb-tab), и в «Аптечке» (действие «Справка» у конкретного медикамента).
 */
@Component({
  selector: 'app-kb-card',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './kb-card.component.html',
})
export class KbCardComponent {
  readonly card = input.required<KbMedicationCard>();
}
