import { Component, inject, isDevMode } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DevLoggerService } from '../../services/dev-logger.service';

@Component({
  selector: 'app-dev-panel',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './dev-panel.component.html',
  styleUrl: './dev-panel.component.scss',
})
export class DevPanelComponent {
  readonly devMode = isDevMode();
  readonly logger = inject(DevLoggerService);

  open = false;
  showSettings = false;

  toggle(): void {
    this.open = !this.open;
  }

  toggleSettings(event: Event): void {
    event.stopPropagation();
    this.showSettings = !this.showSettings;
  }

  levelClass(level: string): string {
    return level === 'warn' ? 'lvl-warn' : level === 'error' ? 'lvl-error' : '';
  }

  formatTime(date: Date): string {
    return date.toTimeString().slice(0, 8);
  }
}
