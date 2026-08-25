import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { AdminApiService, AdminSystemStats } from '../../../services/admin-api.service';

@Component({
  selector: 'app-admin-system',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './admin-system.component.html',
})
export class AdminSystemComponent implements OnInit {
  private readonly api = inject(AdminApiService);

  readonly data = signal<AdminSystemStats | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.data.set(await this.api.getSystemStats());
    } catch {
      this.error.set('Не удалось загрузить статистику системы.');
    } finally {
      this.loading.set(false);
    }
  }
}
