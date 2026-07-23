import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { AuthService, LinkTelegramStart } from '../../services/auth.service';
import { ToastService } from '../../shared/toast/toast.service';

const LINK_POLL_INTERVAL_MS = 4000;

/** Настройки аккаунта (задача 2.3/2.4): экспорт, удаление, привязка email/Telegram, выход. */
@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [FormsModule, RouterLink, DatePipe],
  templateUrl: './settings.component.html',
})
export class SettingsComponent implements OnInit, OnDestroy {
  readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  readonly busy = signal(false);
  readonly linkStep = signal<'idle' | 'code'>('idle');
  readonly deleteConfirmVisible = signal(false);
  readonly telegramLink = signal<LinkTelegramStart | null>(null);

  linkEmail = '';
  linkCode = '';
  linkPin = '';
  deleteConfirmText = '';

  private pollHandle?: ReturnType<typeof setInterval>;

  async ngOnInit(): Promise<void> {
    await this.auth.loadMe();
  }

  ngOnDestroy(): void {
    clearInterval(this.pollHandle);
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

  async startLinkTelegram(): Promise<void> {
    await this.run(async () => {
      try {
        const result = await this.auth.linkTelegramStart();
        this.telegramLink.set(result);
        this.startPolling();
      } catch (e) {
        if (e instanceof HttpErrorResponse && e.status === 503) {
          this.toast.error('Telegram-бот сейчас недоступен — попробуйте позже');
          return;
        }
        if (e instanceof HttpErrorResponse && e.error?.code === 'already_linked') {
          this.toast.info('Telegram уже привязан к этому аккаунту');
          await this.auth.loadMe();
          return;
        }
        throw e;
      }
    });
  }

  private startPolling(): void {
    clearInterval(this.pollHandle);
    this.pollHandle = setInterval(async () => {
      const me = await this.auth.loadMe();
      if (me?.hasTelegram) {
        clearInterval(this.pollHandle);
        this.telegramLink.set(null);
        this.toast.success('Telegram привязан');
      }
    }, LINK_POLL_INTERVAL_MS);
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

  /**
   * Через HttpClient (не <a href download>) — обычная навигация браузера не идёт через
   * authInterceptor и не получает Telegram-заголовок авторизации; в Mini App это раньше
   * приводило к 401 и скачиванию файла с текстом ошибки вместо архива.
   */
  async exportData(): Promise<void> {
    await this.run(async () => {
      const blob = await this.auth.exportAccountData();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'familyhub-export.zip';
      a.click();
      URL.revokeObjectURL(url);
    });
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
