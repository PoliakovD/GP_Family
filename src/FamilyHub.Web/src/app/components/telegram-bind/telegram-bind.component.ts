import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { AuthService } from '../../services/auth.service';

type Step = 'email' | 'code';

/**
 * Первичная привязка Telegram Mini App к email-аккаунту (TelegramBindingService на бэкенде):
 * TelegramId ещё не связан ни с одним User — TelegramMiniAppAuthenticationHandler (lookup-only)
 * отклоняет такие запросы, пока привязка не пройдена. Форма — только email + код подтверждения,
 * без пароля: если email совпадает с уже существующим PWA-аккаунтом, вся его история
 * (семьи/анализы/аптечка) становится доступна из Telegram сразу после подтверждения кода, и его
 * пароль остаётся прежним. Если email новый — бэкенд сам создаёт аккаунт и присылает на этот
 * адрес отдельным письмом временный пароль для входа в PWA (сменить его можно через
 * «Забыли пароль?» на форме входа — отдельного UI смены пароля здесь нет). Токенов здесь не
 * выдаётся — у Telegram нет сессии вообще.
 */
@Component({
  selector: 'app-telegram-bind',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './telegram-bind.component.html',
})
export class TelegramBindComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly step = signal<Step>('email');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  email = '';
  code = '';

  async sendCode(): Promise<void> {
    await this.run(async () => {
      await this.auth.telegramSendCode(this.email);
      this.step.set('code');
    });
  }

  async confirmBind(): Promise<void> {
    await this.run(async () => {
      await this.auth.telegramBind(this.email, this.code);
      await this.router.navigate(['/']);
    });
  }

  backToEmail(): void {
    this.error.set(null);
    this.code = '';
    this.step.set('email');
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (e) {
      this.error.set(this.describe(e));
    } finally {
      this.busy.set(false);
    }
  }

  private describe(e: unknown): string {
    if (e instanceof HttpErrorResponse) {
      switch (e.error?.code) {
        case 'invalid_code': return 'Неверный или истёкший код подтверждения.';
        case 'invalid_init_data': return 'Сессия Telegram устарела — перезапустите приложение.';
        case 'email_linked_to_different_telegram': return 'Этот email уже привязан к другому Telegram-аккаунту.';
        case 'telegram_already_bound': return 'Этот Telegram-аккаунт уже привязан — попробуйте перезапустить приложение.';
      }
      if (e.status === 429) return 'Слишком много запросов — подождите немного.';
    }
    return 'Что-то пошло не так. Попробуйте ещё раз.';
  }
}
