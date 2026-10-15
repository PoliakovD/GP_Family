import { Component, input, ChangeDetectionStrategy } from '@angular/core';

@Component({
  selector: 'app-loading-spinner',
  standalone: true,
  templateUrl: './loading-spinner.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './loading-spinner.component.scss',
})
export class LoadingSpinnerComponent {
  readonly text = input('Загрузка…');
}
