import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { ToastService } from '../../shared/toast/toast.service';
import { runBusy } from '../settings/settings-task';

/**
 * Сбор ФИО/ДР/пола (identity rework) — единственный обязательный экран для аккаунта, созданного
 * через Telegram-привязку (TelegramBindingService не собирает профиль из initData, см. её
 * doc-комментарий). Показывается через profileGuard, пока Me.profileComplete === false.
 * По структуре — как ConsentGateComponent (тот же auth-shell, одна форма, один submit).
 */
@Component({
  selector: 'app-profile-setup',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './profile-setup.component.html',
})
export class ProfileSetupComponent {
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly busy = signal(false);

  lastName = '';
  firstName = '';
  middleName = '';
  birthDate = '';
  gender = 0;

  get canSubmit(): boolean {
    return !this.busy() && !!this.lastName.trim() && !!this.firstName.trim() && !!this.birthDate;
  }

  async submit(): Promise<void> {
    if (!this.canSubmit) return;
    await runBusy(this.busy, this.toast, async () => {
      await this.auth.updateProfile({
        lastName: this.lastName.trim(),
        firstName: this.firstName.trim(),
        middleName: this.middleName.trim() || null,
        birthDate: this.birthDate,
        gender: this.gender,
      });
      await this.router.navigate(['/']);
    });
  }
}
