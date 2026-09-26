import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { PageActionService } from '../../services/page-action.service';

/**
 * Хаб «Здоровье» (редизайн навигации): объединяет Аптечку, Анализы и Врачей под одним табом
 * вместо конкурирующих. Настоящие вложенные роуты (не in-page state) — переживают refresh и
 * работают с browser back; дочерние компоненты переиспользуются как есть, просто монтируются в
 * router-outlet этого хаба, а не корневого. «Анализы» и «Врачи» — тонкие Page-обёртки над одной
 * MedicalRecordsPanelComponent с разным MedicalRecordKind (см. medical-records-panel).
 * Расширяемо: «Таймлайн» (этап 6) добавляется сюда же.
 *
 * Редизайн v2.2 — собственная шапка «Здоровье» + таб-бар секций прячутся, когда открыт дочерний
 * экран «одиночного режима» (открытая запись/аптечка) — см. PageActionService.immersive,
 * который такой дочерний компонент выставляет сам (шаблон).
 */
@Component({
    selector: 'app-health-hub',
    imports: [RouterLink, RouterLinkActive, RouterOutlet],
    templateUrl: './health-hub.component.html',
    styleUrl: './health-hub.component.scss'
})
export class HealthHubComponent {
  readonly pageAction = inject(PageActionService);

  readonly sections: { path: string; label: string }[] = [
    { path: 'medications', label: 'Аптечка' },
    { path: 'records', label: 'Анализы' },
    { path: 'visits', label: 'Посещения врачей' },
    { path: 'notes', label: 'Дневник' },
    { path: 'reports', label: 'Отчёты для врача' },
    { path: 'kb', label: 'Справочник' },
    { path: 'indicators', label: 'Показатели анализов' },
  ];
}
