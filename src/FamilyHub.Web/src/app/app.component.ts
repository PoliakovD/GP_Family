import { Component, OnInit, inject } from '@angular/core';
import { Router, RouterOutlet, RouterLink, RouterLinkActive, NavigationStart, NavigationEnd, NavigationError } from '@angular/router';
import { TelegramService } from './services/telegram.service';
import { FamilyStateService } from './services/family-state.service';
import { DevLoggerService } from './services/dev-logger.service';
import { DevPanelComponent } from './components/dev-panel/dev-panel.component';
import { ToastContainerComponent } from './shared/toast/toast-container.component';
import { ConfirmDialogComponent } from './shared/confirm/confirm-dialog.component';
import { LoadingSpinnerComponent } from './shared/loading-spinner/loading-spinner.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    DevPanelComponent,
    ToastContainerComponent,
    ConfirmDialogComponent,
    LoadingSpinnerComponent,
  ],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
  readonly state = inject(FamilyStateService);
  private readonly tg = inject(TelegramService);
  private readonly router = inject(Router);
  private readonly log = inject(DevLoggerService);

  readonly tabs: { id: string; label: string; icon: string }[] = [
    { id: 'families', label: 'Семьи', icon: 'ph-users-three' },
    { id: 'medications', label: 'Аптечка', icon: 'ph-first-aid-kit' },
    { id: 'birthdays', label: 'Дни р.', icon: 'ph-cake' },
    { id: 'records', label: 'Анализы', icon: 'ph-heartbeat' },
    { id: 'notifications', label: 'Оповещ.', icon: 'ph-bell' },
  ];

  ngOnInit(): void {
    this.tg.init();
    this.state.refresh();
    this.subscribeToRouter();
  }

  private subscribeToRouter(): void {
    this.router.events.subscribe((e) => {
      if (e instanceof NavigationStart) {
        this.log.log('nav', 'info', `→ ${e.url}`);
      } else if (e instanceof NavigationEnd) {
        this.log.log('nav', 'info', `✓ ${e.urlAfterRedirects}`);
      } else if (e instanceof NavigationError) {
        this.log.log('nav', 'error', `✗ ${e.url}: ${String(e.error)}`);
      }
    });
  }
}
