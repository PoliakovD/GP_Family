import { Component, HostListener, OnDestroy, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { AuthService } from '../../services/auth.service';
import { HasPendingCodeEntry } from '../../services/pending-code.guard';
import { TelegramService } from '../../services/telegram.service';

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
export class TelegramBindComponent implements HasPendingCodeEntry, OnDestroy {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly tg = inject(TelegramService);

  readonly step = signal<Step>('email');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  /** См. LoginComponent.completed — тот же приём: успешный confirmBind ставит true ПЕРЕД
   * навигацией, иначе pendingCodeGuard переспрашивал бы «прервать?» даже при успехе. */
  private readonly completed = signal(false);

  email = '';
  code = '';

  async sendCode(): Promise<void> {
    await this.run(async () => {
      await this.auth.telegramSendCode(this.email);
      this.step.set('code');
      // Реальный Telegram Mini App: аппаратный «назад» на Android сворачивает/закрывает
      // приложение мимо Angular Router — popstate-guard (pendingCodeGuard) его не видит.
      // Нативный эквивалент — системное подтверждение Telegram.
      this.tg.enableClosingConfirmation();
    });
  }

  async confirmBind(): Promise<void> {
    await this.run(async () => {
      const profileRequired = await this.auth.telegramBind(this.email, this.code);
      this.completed.set(true);
      this.tg.disableClosingConfirmation();
      await this.router.navigate([profileRequired ? '/profile-setup' : '/']);
    });
  }

  backToEmail(): void {
    this.error.set(null);
    this.code = '';
    this.step.set('email');
    this.tg.disableClosingConfirmation();
  }

  /** pendingCodeGuard (CanDeactivate, браузер/PWA-доступ к /telegram-bind) — см. doc-комментарий там. */
  hasPendingCodeEntry(): boolean {
    return !this.completed() && this.step() === 'code';
  }

  /** Guard ловит только навигацию Angular Router — F5/закрытие вкладки идут мимо него
   * (в самом Mini App это дополнительно перехватывает Telegram.WebApp — см. sendCode). */
  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(e: BeforeUnloadEvent): void {
    if (this.hasPendingCodeEntry()) e.preventDefault();
  }

  ngOnDestroy(): void {
    // Страховка: если компонент уничтожен в обход confirmBind/backToEmail (например,
    // pendingCodeGuard пропустил уход после подтверждения диалога) — не оставляем Mini App
    // с навсегда включённым системным подтверждением закрытия.
    this.tg.disableClosingConfirmation();
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
