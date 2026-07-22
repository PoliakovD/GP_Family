import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { AuthService } from '../../services/auth.service';

type Step = 'login' | 'register-email' | 'register-code';

/** PWA-вход (этап 2 п.2.4): email+PIN и регистрация степпером email → код → PIN. */
@Component({
  selector: 'app-login',
  standalone: true,
  imports: [FormsModule, RouterLink],
  templateUrl: './login.component.html',
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly step = signal<Step>('login');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  email = '';
  pin = '';
  code = '';
  displayName = '';

  async login(): Promise<void> {
    await this.run(async () => {
      await this.auth.login(this.email, this.pin);
      await this.router.navigate(['/']);
    });
  }

  startRegistration(): void {
    this.error.set(null);
    this.step.set('register-email');
  }

  async sendCode(): Promise<void> {
    await this.run(async () => {
      await this.auth.registerStart(this.email);
      this.step.set('register-code');
    });
  }

  async confirmRegistration(): Promise<void> {
    await this.run(async () => {
      await this.auth.registerConfirm(this.email, this.code, this.pin, this.displayName || null);
      await this.router.navigate(['/consent']);
    });
  }

  backToLogin(): void {
    this.error.set(null);
    this.step.set('login');
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
        case 'invalid_credentials': return 'Неверный email или PIN.';
        case 'locked_out': return 'Слишком много попыток — вход временно заблокирован. Попробуйте через 15 минут.';
        case 'invalid_code': return 'Неверный или истёкший код подтверждения.';
        case 'email_taken': return 'Этот email уже зарегистрирован — войдите с PIN-кодом.';
        case 'weak_pin': return 'PIN должен состоять из 4–8 цифр.';
      }
      if (e.status === 429) return 'Слишком много запросов — подождите немного.';
    }
    return 'Что-то пошло не так. Попробуйте ещё раз.';
  }
}
