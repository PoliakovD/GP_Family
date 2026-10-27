import { Component, inject, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FamilyStateService } from '../../services/family-state.service';
import { BirthdaysPanelComponent } from '../birthdays-panel/birthdays-panel.component';

@Component({
    selector: 'app-birthdays-tab',
    imports: [BirthdaysPanelComponent, RouterLink],
    changeDetection: ChangeDetectionStrategy.Eager,
    templateUrl: './birthdays-tab.component.html'
})
export class BirthdaysTabComponent {
  readonly state = inject(FamilyStateService);
}
