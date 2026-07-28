import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { AuthService } from '../../../services/auth.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { runBusy } from '../settings-task';

/** Вкладка «Данные»: политика конфиденциальности, выгрузка данных, удаление аккаунта (152-ФЗ). */
@Component({
  selector: 'app-settings-data',
  standalone: true,
  imports: [FormsModule, RouterLink],
  templateUrl: './settings-data.component.html',
})
export class SettingsDataComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  readonly busy = signal(false);
  readonly deleteConfirmVisible = signal(false);
  deleteConfirmText = '';

  /**
   * Через HttpClient (не <a href download>) — обычная навигация браузера не идёт через
   * authInterceptor и не получает Telegram-заголовок авторизации; в Mini App это раньше
   * приводило к 401 и скачиванию файла с текстом ошибки вместо архива.
   */
  async exportData(): Promise<void> {
    await runBusy(this.busy, this.toast, async () => {
      const blob = await this.auth.exportAccountData();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'familyhub-export.zip';
      a.click();
      URL.revokeObjectURL(url);
    });
  }

  async deleteAccount(): Promise<void> {
    if (this.deleteConfirmText !== 'УДАЛИТЬ') return;
    await runBusy(this.busy, this.toast, async () => {
      try {
        await this.auth.deleteAccount();
        this.toast.success('Аккаунт и все данные удалены');
        await this.router.navigate(['/login']);
      } catch (e) {
        if (e instanceof HttpErrorResponse && e.error?.code === 'last_admin') {
          this.toast.error('Вы последний админ в семье с участниками — сначала передайте права или удалите семью');
          return;
        }
        throw e;
      }
    });
  }
}
