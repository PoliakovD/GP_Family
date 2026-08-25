import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { AdminApiService, AdminStorageStats } from '../../../services/admin-api.service';

@Component({
  selector: 'app-admin-storage',
  standalone: true,
  imports: [DatePipe, DecimalPipe],
  templateUrl: './admin-storage.component.html',
})
export class AdminStorageComponent implements OnInit {
  private readonly api = inject(AdminApiService);

  readonly data = signal<AdminStorageStats | null>(null);
  readonly loading = signal(true);
  readonly recalculating = signal(false);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.load(false);
  }

  async load(recalculate: boolean): Promise<void> {
    (recalculate ? this.recalculating : this.loading).set(true);
    this.error.set(null);
    try {
      this.data.set(await this.api.getStorageStats(recalculate));
    } catch {
      this.error.set('Не удалось загрузить статистику хранилища.');
    } finally {
      this.loading.set(false);
      this.recalculating.set(false);
    }
  }

  /** МиБ — размеры и бакета, и БД идут в байтах, читать сырыми числами неудобно. */
  toMib(bytes: number): number {
    return bytes / (1024 * 1024);
  }
}
