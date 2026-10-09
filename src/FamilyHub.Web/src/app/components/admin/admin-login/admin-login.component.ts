import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { AdminApiService } from '../../../services/admin-api.service';

/** Форма входа в админ-панель (ADR-0009) — отдельный логин/пароль из Admin:User/Password (.env),
 * не связан с обычным PWA-аккаунтом. Живёт на admin.{PUBLIC_DOMAIN} за периметром WireGuard. */
@Component({
    selector: 'app-admin-login',
    imports: [FormsModule],
    templateUrl: './admin-login.component.html'
})
export class AdminLoginComponent {
  private readonly api = inject(AdminApiService);
  private readonly router = inject(Router);

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  user = '';
  password = '';
  totp = '';

  async login(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.api.login(this.user, this.password, this.totp.trim());
      await this.router.navigate(['/admin']);
    } catch (e) {
      this.error.set(this.describeError(e));
    } finally {
      this.busy.set(false);
    }
  }

  private describeError(e: unknown): string {
    if (!(e instanceof HttpErrorResponse)) return 'Что-то пошло не так. Попробуйте ещё раз.';
    if (e.status === 401) return 'Неверный логин, пароль или код из приложения.';
    // Блокировка после серии неудачных входов (сервер: AdminLoginThrottle) или общий rate-limit "auth".
    if (e.status === 429) return 'Слишком много попыток входа. Попробуйте через 15 минут.';
    return 'Что-то пошло не так. Попробуйте ещё раз.';
  }
}
