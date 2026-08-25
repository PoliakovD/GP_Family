import { Component, OnInit, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { AdminApiService, AdminOverview } from '../../../services/admin-api.service';

@Component({
  selector: 'app-admin-overview',
  standalone: true,
  imports: [DecimalPipe],
  templateUrl: './admin-overview.component.html',
})
export class AdminOverviewComponent implements OnInit {
  private readonly api = inject(AdminApiService);

  readonly data = signal<AdminOverview | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.data.set(await this.api.getOverview());
    } catch {
      this.error.set('Не удалось загрузить статистику.');
    } finally {
      this.loading.set(false);
    }
  }
}
