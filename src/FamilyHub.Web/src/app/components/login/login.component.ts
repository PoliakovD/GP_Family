import { Component, HostListener, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../services/auth.service';
import { HasPendingCodeEntry } from '../../services/pending-code.guard';
import { CookieConsentService } from '../../shared/cookie-banner/cookie-consent.service';
import { ModalComponent } from '../../shared/modal/modal.component';
import { PASSWORD_PATTERN } from '../settings/settings-task';

type Step = 'login' | 'register-details' | 'register-code' | 'reset-password-email' | 'reset-password-code';
type UsernameStatus = 'idle' | 'checking' | 'free' | 'taken' | 'invalid';

/** Формат видимого username — зеркалит UsernameRules на бэкенде (единый источник истины — сервер). */
const USERNAME_PATTERN = /^[a-z][a-z0-9_]{4,31}$/;
const USERNAME_CHECK_DEBOUNCE_MS = 400;

/**
 * PWA-вход (этап 2 п.2.4): email+пароль, регистрация степпером email/username/имя/пароль → код,
 * и восстановление забытого пароля тем же email-кодом. Код регистрации запрашивается ПОСЛЕДНИМ
 * шагом, сразу перед подтверждением: он живёт всего 10 минут, и если запросить его до
 * заполнения остальной формы, пользователь рискует не уложиться (или потерять код при
 * случайном обновлении страницы посреди заполнения).
 */
@Component({
  selector: 'app-login',
  standalone: true,
  imports: [FormsModule, ModalComponent],
  templateUrl: './login.component.html',
})
export class LoginComponent implements HasPendingCodeEntry {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly http = inject(HttpClient);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly cookieConsent = inject(CookieConsentService);

  readonly step = signal<Step>('login');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  /** Успешный confirm ставит true ПЕРЕД навигацией — без этого pendingCodeGuard переспрашивал
   * бы «прервать ввод кода?» даже при УСПЕШНОМ завершении регистрации/сброса пароля (на момент
   * router.navigate() step() всё ещё 'register-code'/'reset-password-code'). */
  private readonly completed = signal(false);
  /** Код последней ошибки (см. backend `{ code: "..." }`) — для условного UI (например, CTA на email_taken). */
  readonly errorCode = signal<string | null>(null);
  readonly usernameStatus = signal<UsernameStatus>('idle');

  /** На форме входа объясняем, что сессионный cookie строго необходим, если раньше отклонили баннер. */
  readonly showCookieNotice = computed(() => this.cookieConsent.choice() === 'declined');

  /**
   * Тексты политики/согласия на форме регистрации показываем МОДАЛКОЙ, а не ссылкой на
   * отдельный роут (`target="_blank"`): в PWA, установленном на телефон/десктоп в standalone-
   * режиме, `target="_blank"` для ссылки на тот же origin у части браузеров открывает не новую
   * вкладку, а НАВИГАЦИЮ В ТОМ ЖЕ ОКНЕ — форма регистрации разрушается, и «назад» возвращает уже
   * не на неё, а на чистый /login (все введённые email/пароль/username потеряны). Модалка держит
   * пользователя на той же странице/том же компоненте — данные формы никогда не пропадают.
   */
  readonly legalModalTitle = signal('');
  readonly legalModalHtml = signal<SafeHtml | null>(null);
  readonly legalModalBusy = signal(false);
  readonly legalModalOpen = computed(() => this.legalModalTitle() !== '');

  email = '';
  password = '';
  code = '';
  username = '';
  displayName = '';
  privacyAccepted = false;
  /** Согласие на обработку ПДн (общее) — отдельно от privacyAccepted выше: политика
   * конфиденциальности и согласие на обработку ПДн — разные документы (ст. 9 152-ФЗ). */
  pdnConsentAgreed = false;
  /** Отдельное обязательное согласие на спецкатегорию — сведения о здоровье (ч. 2 ст. 10
   * 152-ФЗ), тот же приём, что и в ConsentGateComponent. */
  pdnSpecialCategoryAgreed = false;

  private usernameCheckTimer?: ReturnType<typeof setTimeout>;
  private usernameCheckToken = 0;

  /** Только для полей, где пароль ЗАДАЁТСЯ (регистрация, сброс) — вход не гейтится силой пароля,
   * иначе аккаунты с уже установленным (в т.ч. старым, короче 8 симв.) паролем не смогли бы войти. */
  get isPasswordValid(): boolean {
    return PASSWORD_PATTERN.test(this.password);
  }

  get canSubmitDetails(): boolean {
    return !this.busy() && this.privacyAccepted && this.pdnConsentAgreed && this.pdnSpecialCategoryAgreed
      && this.usernameStatus() === 'free' && this.isPasswordValid;
  }

  get canSubmitNewPassword(): boolean {
    return !this.busy() && this.isPasswordValid;
  }

  async login(): Promise<void> {
    await this.run(async () => {
      await this.auth.login(this.email, this.password);
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
      await this.auth.registerConfirm(this.email, this.code, this.password, this.username, this.displayName || null);
      this.completed.set(true);
      // Оба обязательных чекбокса ПДн-согласия отмечены на предыдущем шаге (register-details,
      // см. canSubmitDetails) — записываем принятие сразу же, пока сессия свежая, той версией
      // текста, которая ДЕЙСТВИТЕЛЬНО актуальна на сервере (не кэшированной с момента открытия
      // формы: между заполнением формы и этим моментом текст мог смениться).
      // Если запрос не удался (сеть) — не блокируем регистрацию: ConsentRequiredFilter и
      // consentGuard всё равно перехватят на защищённых роутах и покажут /consent как обычно.
      const accepted = await this.acceptPdnConsent();
      await this.router.navigate([accepted ? '/' : '/consent']);
    });
  }

  private async acceptPdnConsent(): Promise<boolean> {
    try {
      const current = await this.auth.getConsentText();
      await this.auth.acceptConsent(current.version);
      return true;
    } catch {
      return false;
    }
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
    this.password = ''; // пароль, введённый для регистрации, — не тот, что нужен для входа в старый аккаунт
    this.step.set('login');
  }

  startResetPassword(): void {
    this.error.set(null);
    this.step.set('reset-password-email');
  }

  async sendResetCode(): Promise<void> {
    await this.run(async () => {
      await this.auth.resetPasswordStart(this.email);
      this.step.set('reset-password-code');
    });
  }

  async confirmResetPassword(): Promise<void> {
    if (!this.canSubmitNewPassword) return;
    await this.run(async () => {
      await this.auth.resetPasswordConfirm(this.email, this.code, this.password);
      this.completed.set(true);
      await this.router.navigate(['/']);
    });
  }

  /** pendingCodeGuard (CanDeactivate) — см. doc-комментарий там. */
  hasPendingCodeEntry(): boolean {
    return !this.completed() && (this.step() === 'register-code' || this.step() === 'reset-password-code');
  }

  /** Guard ловит только навигацию Angular Router — F5/закрытие вкладки идут мимо него. */
  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(e: BeforeUnloadEvent): void {
    if (this.hasPendingCodeEntry()) e.preventDefault(); // текст диалога задаёт браузер, кастомный игнорируется
  }

  backToLogin(): void {
    this.error.set(null);
    this.errorCode.set(null);
    this.step.set('login');
  }

  async showLegalText(kind: 'privacy' | 'consent'): Promise<void> {
    this.legalModalBusy.set(true);
    this.legalModalTitle.set(kind === 'privacy' ? 'Политика конфиденциальности' : 'Согласие на обработку персональных данных');
    this.legalModalHtml.set(null);
    try {
      const html = kind === 'privacy'
        ? await firstValueFrom(this.http.get('/api/legal/privacy-policy', { responseType: 'text' }))
        : (await this.auth.getConsentText()).text;
      // ИНВАРИАНТ: bypassSecurityTrustHtml здесь безопасен ТОЛЬКО пока текст — embedded-ресурс
      // бэкенда, не редактируемый через CMS/админку контент, как и в ConsentTextComponent/
      // PrivacyComponent, которые показывают тот же текст на своих роутах. См. аудит
      // module-review-2026-08-02/08-web-frontend-angular.md, находка 5.
      this.legalModalHtml.set(this.sanitizer.bypassSecurityTrustHtml(html));
    } catch {
      this.legalModalTitle.set('');
      this.error.set('Не удалось загрузить текст. Попробуйте ещё раз.');
    } finally {
      this.legalModalBusy.set(false);
    }
  }

  closeLegalModal(): void {
    this.legalModalTitle.set('');
    this.legalModalHtml.set(null);
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
        case 'invalid_credentials': return 'Неверный email или пароль.';
        case 'locked_out': return 'Слишком много попыток — вход временно заблокирован. Попробуйте через 15 минут.';
        case 'invalid_code': return 'Неверный или истёкший код подтверждения.';
        case 'email_taken': return 'Этот email уже зарегистрирован.';
        case 'weak_password': return 'Пароль — минимум 8 символов, обязательно строчная и заглавная латинские буквы и цифра.';
        case 'invalid_username': return 'Некорректный username — 5–32 символа: латиница, цифры, «_», с буквы.';
        case 'username_taken': return 'Этот username уже занят — выберите другой.';
      }
      if (e.status === 429) return 'Слишком много запросов — подождите немного.';
    }
    return 'Что-то пошло не так. Попробуйте ещё раз.';
  }
}
