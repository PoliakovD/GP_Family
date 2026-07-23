import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { AuthService } from '../../services/auth.service';
import { CookieConsentService } from '../../shared/cookie-banner/cookie-consent.service';

type Step = 'login' | 'register-details' | 'register-code' | 'reset-pin-email' | 'reset-pin-code';
type UsernameStatus = 'idle' | 'checking' | 'free' | 'taken' | 'invalid';

/** Формат видимого username — зеркалит UsernameRules на бэкенде (единый источник истины — сервер). */
const USERNAME_PATTERN = /^[a-z][a-z0-9_]{4,31}$/;
const USERNAME_CHECK_DEBOUNCE_MS = 400;

/**
 * PWA-вход (этап 2 п.2.4): email+PIN, регистрация степпером email/username/имя/PIN → код,
 * и восстановление забытого PIN тем же email-кодом. Код регистрации запрашивается ПОСЛЕДНИМ
 * шагом, сразу перед подтверждением: он живёт всего 10 минут, и если запросить его до
 * заполнения остальной формы, пользователь рискует не уложиться (или потерять код при
 * случайном обновлении страницы посреди заполнения).
 */
@Component({
  selector: 'app-login',
  standalone: true,
  imports: [FormsModule, RouterLink],
  templateUrl: './login.component.html',
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly cookieConsent = inject(CookieConsentService);

  readonly step = signal<Step>('login');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  /** Код последней ошибки (см. backend `{ code: "..." }`) — для условного UI (например, CTA на email_taken). */
  readonly errorCode = signal<string | null>(null);
  readonly usernameStatus = signal<UsernameStatus>('idle');

  /** На форме входа объясняем, что сессионный cookie строго необходим, если раньше отклонили баннер. */
  readonly showCookieNotice = computed(() => this.cookieConsent.choice() === 'declined');

  email = '';
  pin = '';
  code = '';
  username = '';
  displayName = '';
  privacyAccepted = false;

  private usernameCheckTimer?: ReturnType<typeof setTimeout>;
  private usernameCheckToken = 0;

  get canSubmitDetails(): boolean {
    return !this.busy() && this.privacyAccepted && this.usernameStatus() === 'free';
  }

  async login(): Promise<void> {
    await this.run(async () => {
      await this.auth.login(this.email, this.pin);
      await this.router.navigate(['/']);
    });
  }

  startRegistration(): void {
    this.error.set(null);
    this.step.set('register-details');
  }

  onUsernameInput(value: string): void {
    // Нормализация как на бэкенде (UsernameRules.Normalize) — по мере ввода, чтобы
    // пользователь сразу видел итоговый вид хэндла.
    this.username = value.trim().toLowerCase();

    if (!this.username) {
      this.usernameStatus.set('idle');
      return;
    }
    if (!USERNAME_PATTERN.test(this.username)) {
      this.usernameStatus.set('invalid');
      return;
    }

    this.usernameStatus.set('checking');
    const token = ++this.usernameCheckToken;
    clearTimeout(this.usernameCheckTimer);
    this.usernameCheckTimer = setTimeout(async () => {
      try {
        const available = await this.auth.checkUsernameAvailable(this.username);
        if (token !== this.usernameCheckToken) return; // устарел — пользователь уже печатает дальше
        this.usernameStatus.set(available ? 'free' : 'taken');
      } catch {
        if (token === this.usernameCheckToken) this.usernameStatus.set('idle');
      }
    }, USERNAME_CHECK_DEBOUNCE_MS);
  }

  /** Все поля заполнены и провалидированы — только теперь запрашиваем код (10-минутное окно). */
  async submitDetails(): Promise<void> {
    if (!this.canSubmitDetails) return;
    await this.run(async () => {
      await this.auth.registerStart(this.email);
      this.step.set('register-code');
    });
  }

  async confirmRegistration(): Promise<void> {
    await this.run(async () => {
      await this.auth.registerConfirm(this.email, this.code, this.pin, this.username, this.displayName || null);
      await this.router.navigate(['/consent']);
    });
  }

  /** С шага «код» — назад к деталям (например, поправить email), без потери введённого. */
  backToDetails(): void {
    this.error.set(null);
    this.errorCode.set(null);
    this.code = '';
    this.step.set('register-details');
  }

  /** CTA при email_taken на шаге кода: email уже принадлежит существующему аккаунту — сразу на вход. */
  useExistingAccount(): void {
    this.error.set(null);
    this.errorCode.set(null);
    this.pin = ''; // PIN, введённый для регистрации, — не тот, что нужен для входа в старый аккаунт
    this.step.set('login');
  }

  startResetPin(): void {
    this.error.set(null);
    this.step.set('reset-pin-email');
  }

  async sendResetCode(): Promise<void> {
    await this.run(async () => {
      await this.auth.resetPinStart(this.email);
      this.step.set('reset-pin-code');
    });
  }

  async confirmResetPin(): Promise<void> {
    await this.run(async () => {
      await this.auth.resetPinConfirm(this.email, this.code, this.pin);
      await this.router.navigate(['/']);
    });
  }

  backToLogin(): void {
    this.error.set(null);
    this.errorCode.set(null);
    this.step.set('login');
  }

  acceptCookies(): void {
    this.cookieConsent.setChoice('accepted');
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.errorCode.set(null);
    try {
      await action();
    } catch (e) {
      this.errorCode.set(e instanceof HttpErrorResponse ? (e.error?.code ?? null) : null);
      this.error.set(this.describe(e));
    } finally {
      this.busy.set(false);
    }
  }

  private describe(e: unknown): string {
    if (e instanceof HttpErrorResponse) {
      switch (e.error?.code) {
        case 'invalid_credentials': return 'Неверный email или PIN.';
        case 'locked_out': return 'Слишком много попыток — вход временно заблокирован. Попробуйте через 15 минут.';
        case 'invalid_code': return 'Неверный или истёкший код подтверждения.';
        case 'email_taken': return 'Этот email уже зарегистрирован.';
        case 'weak_pin': return 'PIN должен состоять из 4–8 цифр.';
        case 'invalid_username': return 'Некорректный username — 5–32 символа: латиница, цифры, «_», с буквы.';
        case 'username_taken': return 'Этот username уже занят — выберите другой.';
      }
      if (e.status === 429) return 'Слишком много запросов — подождите немного.';
    }
    return 'Что-то пошло не так. Попробуйте ещё раз.';
  }
}
