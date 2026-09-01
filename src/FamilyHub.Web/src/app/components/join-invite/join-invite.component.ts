import { Component, Input, OnInit, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiError, ApiService } from '../../services/api.service';
import { AuthService } from '../../services/auth.service';
import { TelegramService } from '../../services/telegram.service';
import { PendingInviteService } from '../../services/pending-invite.service';
import { ToastService } from '../../shared/toast/toast.service';
import type { InvitePreview } from '../../models/types';

type PreviewState = 'loading' | 'valid' | 'not_found' | 'revoked' | 'expired' | 'exhausted';

/**
 * Публичный лендинг приглашения (/join/:code, без гардов) — веб-альтернатива Telegram-инвайту
 * (см. FamilyDetailsComponent). Гостю показывает превью (без персональных данных участников,
 * см. InviteEndpoints.GetPreview) и предлагает создать аккаунт/войти; уже вошедшему —
 * присоединиться сразу. Код погашается автоматически после аутентификации, если гость ушёл на
 * /login или /telegram-bind — см. PendingInviteService/AppComponent.
 */
@Component({
  selector: 'app-join-invite',
  standalone: true,
  templateUrl: './join-invite.component.html',
})
export class JoinInviteComponent implements OnInit {
  @Input() code!: string;

  private readonly api = inject(ApiService);
  readonly auth = inject(AuthService);
  private readonly tg = inject(TelegramService);
  private readonly pendingInvite = inject(PendingInviteService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly state = signal<PreviewState>('loading');
  readonly preview = signal<InvitePreview | null>(null);
  readonly authChecked = signal(false);
  readonly busy = signal(false);

  async ngOnInit(): Promise<void> {
    await Promise.all([this.loadPreview(), this.resolveAuth()]);
  }

  private async loadPreview(): Promise<void> {
    try {
      const preview = await this.api.getInvitePreview(this.code);
      this.preview.set(preview);
      this.state.set('valid');
    } catch (e) {
      if (e instanceof ApiError) {
        if (e.status === 404) { this.state.set('not_found'); return; }
        if (e.status === 409) {
          switch (e.message) {
            case 'revoked': this.state.set('revoked'); return;
            case 'expired': this.state.set('expired'); return;
            default: this.state.set('exhausted'); return;
          }
        }
      }
      this.state.set('not_found');
    }
  }

  /** Telegram Mini App без привязки — сразу на /telegram-bind, минуя /login (которого в Telegram
   * не существует); PWA/dev — обычная проверка сессии. */
  private async resolveAuth(): Promise<void> {
    if (this.auth.mode === 'telegram' && this.tg.isInsideTelegram()) {
      const bound = this.auth.telegramBound() ?? (await this.auth.ensureTelegramBound());
      if (!bound) {
        this.pendingInvite.set(this.code);
        await this.router.navigate(['/telegram-bind']);
        return;
      }
    }

    if (this.auth.me() === null) await this.auth.loadMe();
    this.authChecked.set(true);
  }

  goToLogin(): void {
    this.pendingInvite.set(this.code);
    void this.router.navigate(['/login']);
  }

  goToRegister(): void {
    this.pendingInvite.set(this.code);
    void this.router.navigate(['/login'], { queryParams: { intent: 'register' } });
  }

  async joinNow(): Promise<void> {
    this.busy.set(true);
    try {
      const result = await this.api.redeemInvite(this.code);
      this.toast.success(
        result.status === 'joined'
          ? 'Вы присоединились к семье.'
          : 'Заявка отправлена, ожидайте подтверждения администратором.',
      );
      await this.router.navigate(result.familyId ? ['/families', result.familyId] : ['/home']);
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось погасить приглашение.');
    } finally {
      this.busy.set(false);
    }
  }
}
