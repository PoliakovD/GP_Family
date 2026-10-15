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
    imports: [RouterLink, RouterLinkActive, RouterOutlet],
    templateUrl: './kb-hub.component.html',
    styles: [`
      .kb { display: flex; flex-direction: column; gap: var(--space-5); }
      .kb-title { margin: 0; font: var(--text-display); letter-spacing: var(--letter-spacing-display); }
      .kb-sections { align-self: flex-start; }
      @media (max-width: 1023px) { .kb-title { font: var(--text-display-mobile); letter-spacing: var(--letter-spacing-display); } }
    `],
})
export class KbHubComponent {
  readonly sections: { path: string; label: string }[] = [
    { path: 'medications', label: 'Медицинские препараты' },
    { path: 'indicators', label: 'Показатели анализов' },
    { path: 'vaccines', label: 'Прививки' },
  ];
}
