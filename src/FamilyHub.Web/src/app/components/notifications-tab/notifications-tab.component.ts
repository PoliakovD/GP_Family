import { Component, OnInit, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import { NotificationType, type AppNotification } from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';

const TYPE_LABEL: Record<number, string> = {
  [NotificationType.MedicationExpiringSoon]: 'Срок годности скоро истекает',
  [NotificationType.MedicationExpired]: 'Срок годности истёк',
  [NotificationType.BirthdayUpcoming]: 'Скоро день рождения',
};

@Component({
  selector: 'app-notifications-tab',
  standalone: true,
  imports: [FormsModule, LoadingSpinnerComponent],
  templateUrl: './notifications-tab.component.html',
})
export class NotificationsTabComponent implements OnInit {
  private readonly api = inject(ApiService);

  items: AppNotification[] = [];
  unreadOnly = false;
  error: string | null = null;
  loading = true;

  ngOnInit(): void {
    this.refresh();
  }

  async refresh(): Promise<void> {
    this.loading = true;
    try {
      this.items = await this.api.getNotifications(this.unreadOnly);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить оповещения.';
    } finally {
      this.loading = false;
    }
  }

  async handleMarkRead(id: string): Promise<void> {
    try {
      await this.api.markNotificationRead(id);
      await this.refresh();
    } catch (err) {
      this.error =
        err instanceof ApiError ? err.message : 'Не удалось отметить как прочитанное.';
    }
  }

  typeLabel(type: number): string {
    return TYPE_LABEL[type] ?? 'Оповещение';
  }
}
