import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { AuthService } from '../../services/auth.service';
import { ToastService } from '../../shared/toast/toast.service';

/** Настройки аккаунта (задача 2.3/2.4): экспорт, удаление, привязка email, выход. */
@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [FormsModule, RouterLink],
  templateUrl: './settings.component.html',
})
export class SettingsComponent implements OnInit {
  readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  readonly busy = signal(false);
  readonly linkStep = signal<'idle' | 'code'>('idle');
  readonly deleteConfirmVisible = signal(false);

  linkEmail = '';
  linkCode = '';
  linkPin = '';
  deleteConfirmText = '';

  async ngOnInit(): Promise<void> {
    await this.auth.loadMe();
  }

  async startLinkEmail(): Promise<void> {
    await this.run(async () => {
      await this.auth.linkEmailStart(this.linkEmail);
      this.linkStep.set('code');
      this.toast.info('Код отправлен на почту');
    });
  }

  async confirmLinkEmail(): Promise<void> {
    await this.run(async () => {
      await this.auth.linkEmailConfirm(this.linkEmail, this.linkCode, this.linkPin);
      this.linkStep.set('idle');
      this.toast.success('Email привязан — теперь можно входить из браузера');
    });
  }

  async deleteAccount(): Promise<void> {
    if (this.deleteConfirmText !== 'УДАЛИТЬ') return;
    await this.run(async () => {
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

  async logout(): Promise<void> {
    await this.auth.logout();
    await this.router.navigate(['/login']);
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    try {
      await action();
    } catch {
      this.toast.error('Не получилось — попробуйте ещё раз');
    } finally {
      this.busy.set(false);
    }
  }
}
