import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

/**
 * Мини-хаб «Справочник» (редизайн v2, PR4) — буквальная копия HealthHubComponent на другой
 * набор дочерних роутов: «Открыть в справочнике» из статьи показателя должно вести на отдельный
 * сегмент, а не в общий список с препаратами. medications — прежний KbTabComponent без изменений
 * содержимого; indicators — новый справочник показателей (kb-analyte-tab).
 */
@Component({
  selector: 'app-kb-hub',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './kb-hub.component.html',
})
export class KbHubComponent {
  readonly sections: { path: string; label: string }[] = [
    { path: 'medications', label: 'Препараты' },
    { path: 'indicators', label: 'Показатели' },
  ];
}
