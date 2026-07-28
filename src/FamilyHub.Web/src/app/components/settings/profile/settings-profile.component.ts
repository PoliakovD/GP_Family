import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { AuthService, LinkTelegramStart } from '../../../services/auth.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { runBusy } from '../settings-task';

const LINK_POLL_INTERVAL_MS = 4000;

/**
 * Вкладка «Профиль»: базовые данные аккаунта + привязка способов входа (email/Telegram).
 * Отвязка Telegram и выход — на вкладке «Безопасность» (settings-security.component.ts).
 */
@Component({
  selector: 'app-settings-profile',
  standalone: true,
  imports: [FormsModule, DatePipe],
  templateUrl: './settings-profile.component.html',
})
export class SettingsProfileComponent implements OnInit, OnDestroy {
  readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);

  readonly busy = signal(false);
  readonly linkStep = signal<'idle' | 'code'>('idle');
  readonly telegramLink = signal<LinkTelegramStart | null>(null);

  linkEmail = '';
  linkCode = '';
  linkPassword = '';

  private pollHandle?: ReturnType<typeof setInterval>;

  async ngOnInit(): Promise<void> {
    await this.auth.loadMe();
  }

  ngOnDestroy(): void {
    clearInterval(this.pollHandle);
  }

  async startLinkEmail(): Promise<void> {
    await runBusy(this.busy, this.toast, async () => {
      await this.auth.linkEmailStart(this.linkEmail);
      this.linkStep.set('code');
      this.toast.info('Код отправлен на почту');
    });
  }

  async confirmLinkEmail(): Promise<void> {
    await runBusy(this.busy, this.toast, async () => {
      await this.auth.linkEmailConfirm(this.linkEmail, this.linkCode, this.linkPassword);
      this.linkStep.set('idle');
      this.toast.success('Email привязан — теперь можно входить из браузера');
    });
  }

  async startLinkTelegram(): Promise<void> {
    await runBusy(this.busy, this.toast, async () => {
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
}
