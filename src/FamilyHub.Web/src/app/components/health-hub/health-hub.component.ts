import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { PageActionService } from '../../services/page-action.service';
import { BreakpointService } from '../../services/breakpoint.service';
import { BackLinkComponent } from '../../shared/back-link/back-link.component';

/**
 * Хаб «Здоровье» (редизайн навигации): объединяет все разделы под одним табом вместо конкурирующих.
 * Настоящие вложенные роуты (не in-page state) — переживают refresh и работают с browser back;
 * дочерние компоненты переиспользуются как есть, просто монтируются в router-outlet этого хаба, а
 * не корневого. «Анализы» и «Врачи» — тонкие Page-обёртки над одной MedicalRecordsPanelComponent с
 * разным MedicalRecordKind (см. medical-records-panel).
 *
 * Редизайн хаба «Здоровье» (макет «Screen - Health hub») — собственный заголовок «Здоровье» и
 * плоский `.seg` из 9 вкладок убраны совсем: навигация теперь идёт через плитки хаба
 * (health-home.component.ts, индексный роут) и сгруппированный сайдбар (app.component.ts
 * healthGroups). На мобиле вместо `.seg` — ссылка «‹ Здоровье» над заголовком дочернего раздела.
 *
 * Редизайн v2.2 — собственная шапка прячется, когда открыт дочерний экран «одиночного режима»
 * (открытая запись/аптечка) — см. PageActionService.immersive, который такой дочерний компонент
 * выставляет сам (шаблон).
 */
@Component({
    selector: 'app-health-hub',
    imports: [RouterOutlet, BackLinkComponent],
    templateUrl: './health-hub.component.html',
})
export class HealthHubComponent {
  readonly pageAction = inject(PageActionService);
  private readonly breakpoints = inject(BreakpointService);
  private readonly router = inject(Router);

  readonly isWide = computed(() => this.breakpoints.tier() === 'wide');

  /** router.url — не сигнал, поэтому для реактивности отслеживаем NavigationEnd явно (тот же приём,
   * что currentUrl в app.component.ts). */
  private readonly currentUrl = signal(this.router.url);
  /** На индексном роуте (хаб сам по себе, health-home.component.ts) «‹ Здоровье» не нужна — мы уже
   * там; ссылка появляется только на дочернем разделе. */
  readonly isHubIndex = computed(() => this.currentUrl() === '/health');

  constructor() {
    this.router.events
      .pipe(filter((e) => e instanceof NavigationEnd), takeUntilDestroyed())
      .subscribe((e) => this.currentUrl.set(e.urlAfterRedirects));
  }

  goToHub(): void {
    void this.router.navigateByUrl('/health');
  }
}
