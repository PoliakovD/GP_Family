import { Component, OnInit, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';
import { TelegramService } from './services/telegram.service';
import { FamilyStateService } from './services/family-state.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [FormsModule, RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
  readonly state = inject(FamilyStateService);
  private readonly tg = inject(TelegramService);

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
  }
}
