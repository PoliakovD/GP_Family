import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

/**
 * Хаб «Здоровье» (редизайн навигации): объединяет Аптечку и Анализы под одним табом вместо
 * двух конкурирующих. Настоящие вложенные роуты (не in-page state) — переживают refresh и
 * работают с browser back; дочерние компоненты (MedicationsTabComponent/MedicalRecordsTabComponent)
 * переиспользуются как есть, просто монтируются в router-outlet этого хаба, а не корневого.
 * Расширяемо: новые разделы («Назначения», «Таймлайн» — этапы 5.3/6) добавляются сюда же.
 */
@Component({
  selector: 'app-health-hub',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './health-hub.component.html',
})
export class HealthHubComponent {
  readonly sections: { path: string; label: string }[] = [
    { path: 'medications', label: 'Аптечка' },
    { path: 'records', label: 'Анализы' },
  ];
}
