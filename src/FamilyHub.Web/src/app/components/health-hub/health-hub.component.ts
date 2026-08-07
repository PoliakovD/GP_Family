import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

/**
 * Хаб «Здоровье» (редизайн навигации): объединяет Аптечку, Анализы и Врачей под одним табом
 * вместо конкурирующих. Настоящие вложенные роуты (не in-page state) — переживают refresh и
 * работают с browser back; дочерние компоненты переиспользуются как есть, просто монтируются в
 * router-outlet этого хаба, а не корневого. «Анализы» и «Врачи» — тонкие Page-обёртки над одной
 * MedicalRecordsPanelComponent с разным MedicalRecordKind (см. medical-records-panel).
 * Расширяемо: «Таймлайн» (этап 6) добавляется сюда же.
 */
@Component({
  selector: 'app-health-hub',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './health-hub.component.html',
  styleUrl: './health-hub.component.scss',
})
export class HealthHubComponent {
  readonly sections: { path: string; label: string }[] = [
    { path: 'medications', label: 'Аптечка' },
    { path: 'records', label: 'Анализы' },
    { path: 'visits', label: 'Врачи' },
    { path: 'kb', label: 'Справочник' },
  ];
}
