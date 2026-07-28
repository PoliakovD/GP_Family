import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService, UserSessionInfo } from '../../../services/auth.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { PASSWORD_PATTERN, describeSettingsError, runBusy } from '../settings-task';

/**
 * Вкладка «Безопасность»: смена пароля, список активных сессий/устройств, отвязка Telegram, выход
 * (текущего устройства и со всех сразу). Привязка email/Telegram — на вкладке «Профиль».
 */
@Component({
  selector: 'app-settings-security',
  standalone: true,
  imports: [FormsModule, DatePipe, RouterLink],
  templateUrl: './settings-security.component.html',
})
export class SettingsSecurityComponent implements OnInit {
  readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly busy = signal(false);
  readonly sessionsBusy = signal(false);
  readonly revokeTelegramConfirmVisible = signal(false);
  readonly sessions = signal<UserSessionInfo[] | null>(null);

  currentPassword = '';
  newPassword = '';
  newPasswordRepeat = '';

  async ngOnInit(): Promise<void> {
    await this.auth.loadMe();
    if (this.auth.mode === 'pwa') {
      await this.loadSessions();
    }
  }

  get passwordFormValid(): boolean {
    return (
      PASSWORD_PATTERN.test(this.newPassword) &&
      this.newPassword === this.newPasswordRepeat &&
      this.currentPassword.length > 0
    );
  }

  async changePassword(): Promise<void> {
    if (!this.passwordFormValid) return;
    await runBusy(this.busy, this.toast, async () => {
      await this.auth.changePassword(this.currentPassword, this.newPassword);
      this.currentPassword = '';
      this.newPassword = '';
      this.newPasswordRepeat = '';
      this.toast.success('Пароль изменён. Остальные устройства вышли из аккаунта.');
      await this.loadSessions();
    });
  }

  private async loadSessions(): Promise<void> {
    try {
      this.sessions.set(await this.auth.getSessions());
    } catch (e) {
      this.toast.error(describeSettingsError(e));
    }
  }

  async revokeSession(id: string): Promise<void> {
    const confirmed = await this.confirm.confirm({
      title: 'Завершить сессию?',
      message: 'Устройство потеряет доступ и будет разлогинено при следующем действии.',
      confirmText: 'Завершить',
      danger: true,
    });
    if (!confirmed) return;

    this.sessionsBusy.set(true);
    try {
      await this.auth.revokeSession(id);
      await this.loadSessions();
      this.toast.success('Сессия завершена.');
    } catch (e) {
      this.toast.error(describeSettingsError(e));
    } finally {
      this.sessionsBusy.set(false);
    }
  }

  async logoutAll(): Promise<void> {
    const confirmed = await this.confirm.confirm({
      title: 'Выйти со всех устройств?',
      message: 'Все активные сессии, включая текущую, будут завершены — потребуется войти заново.',
      confirmText: 'Выйти со всех',
      danger: true,
    });
    if (!confirmed) return;

    await this.auth.logoutAll();
    await this.router.navigate(['/login']);
  }

  /**
   * Если открыто из самого Telegram — сразу после revoke текущий TelegramId больше не находится
   * lookup-only хендлером, эта же сессия перестаёт проходить аутентификацию; ведём на повторную
   * привязку, а не оставляем на странице настроек с последующими молчаливыми 401.
   */
  async revokeTelegram(): Promise<void> {
    this.revokeTelegramConfirmVisible.set(false);
    await runBusy(this.busy, this.toast, async () => {
      await this.auth.revokeTelegram();
      if (this.auth.mode === 'telegram') {
        await this.router.navigate(['/telegram-bind']);
      } else {
        this.toast.success('Telegram отвязан');
      }
    });
  }

  async logout(): Promise<void> {
    await this.auth.logout();
    await this.router.navigate(['/login']);
  }
}
