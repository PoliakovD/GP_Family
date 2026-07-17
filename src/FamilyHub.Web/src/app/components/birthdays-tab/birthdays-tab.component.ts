import { Component, inject } from '@angular/core';
import { FamilyStateService } from '../../services/family-state.service';
import { BirthdaysPanelComponent } from '../birthdays-panel/birthdays-panel.component';

@Component({
  selector: 'app-birthdays-tab',
  standalone: true,
  imports: [BirthdaysPanelComponent],
  templateUrl: './birthdays-tab.component.html',
})
export class BirthdaysTabComponent {
  readonly state = inject(FamilyStateService);
}
