import { Component, inject } from '@angular/core';
import { FamilyStateService } from '../../services/family-state.service';
import { MedkitsPanelComponent } from '../medkits-panel/medkits-panel.component';

@Component({
  selector: 'app-medications-tab',
  standalone: true,
  imports: [MedkitsPanelComponent],
  templateUrl: './medications-tab.component.html',
})
export class MedicationsTabComponent {
  readonly state = inject(FamilyStateService);
}
