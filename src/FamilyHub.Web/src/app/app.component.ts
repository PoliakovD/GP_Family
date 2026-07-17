import { Component, OnInit, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterOutlet, RouterLink, RouterLinkActive, NavigationStart, NavigationEnd, NavigationError } from '@angular/router';
import { TelegramService } from './services/telegram.service';
import { FamilyStateService } from './services/family-state.service';
import { DevLoggerService } from './services/dev-logger.service';
import { DevPanelComponent } from './components/dev-panel/dev-panel.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [FormsModule, RouterOutlet, RouterLink, RouterLinkActive, DevPanelComponent],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
  readonly state = inject(FamilyStateService);
  private readonly tg = inject(TelegramService);
  private readonly router = inject(Router);
  private readonly log = inject(DevLoggerService);

  readonly tabs: { id: string; label: string }[] = [
    { id: 'families', label: 'Семьи' },
    { id: 'medications', label: 'Аптечка' },
    { id: 'birthdays', label: 'Дни рождения' },
    { id: 'records', label: 'Анализы' },
    { id: 'notifications', label: 'Оповещения' },
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
