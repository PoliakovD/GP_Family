import { Component, input, ChangeDetectionStrategy } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Car, Info, Pill, Thermometer, TriangleAlert } from 'lucide';
import type { KbMedicationCard } from '../../models/types';
import { IconComponent } from '../../shared/ui/icon/icon.component';

/**
 * Карточка препарата из общего справочника (этап 4) — Panel (таксономия из
 * patterns/frontend_web.md): нет своего URL, переиспользуется и в разделе «Справочник»
 * (kb-tab), и в «Аптечке» (действие «Справка» у конкретного медикамента). Вид — общие блоки
 * статьи справочника (styles.scss, «Статья справочника»), как у справки по показателю.
 */
@Component({
    changeDetection: ChangeDetectionStrategy.Eager,
    selector: 'app-kb-card',
    imports: [DatePipe, IconComponent],
    templateUrl: './kb-card.component.html',
})
export class KbCardComponent {
  readonly card = input.required<KbMedicationCard>();

  protected readonly icons = { Car, Info, Pill, Thermometer, TriangleAlert };
}
