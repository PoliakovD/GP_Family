import { Component, inject, ChangeDetectionStrategy } from '@angular/core';
import { ToastService } from './toast.service';

@Component({
  selector: 'app-toast-container',
  standalone: true,
  templateUrl: './toast-container.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './toast-container.component.scss',
})
export class ToastContainerComponent {
  readonly toastService = inject(ToastService);

  iconFor(type: string): string {
    return type === 'success' ? '✓' : type === 'error' ? '✕' : 'ℹ';
  }
}
