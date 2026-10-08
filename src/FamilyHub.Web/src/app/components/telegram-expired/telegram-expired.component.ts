import { Component, inject } from '@angular/core';
import { TelegramService } from '../../services/telegram.service';

/**
 * Mini App открыт дольше, чем живёт его initData (Telegram:MaxInitDataAge, 1 ч — аудит
 * security-audit-2026-10, M6): Telegram выдаёт свежие данные только при новом открытии, поэтому
 * единственное действие — закрыть и открыть приложение заново. Раньше такой 401 уводил на экран
 * привязки почты, будто аккаунт отвязан.
 */
@Component({
  selector: 'app-telegram-expired',
  template: `
    <div class="auth-shell">
      <div class="card auth-card elev-md">
        <div class="auth-brand">
          <div class="card-kicker">FamilyHub</div>
          <h2 class="card-title mb-0">Сессия устарела</h2>
        </div>
        <p class="text-muted mb-3">
          Приложение было открыто слишком долго. Для защиты ваших данных закройте его
          и откройте заново из чата с ботом — все данные на месте.
        </p>
        <button type="button" class="btn btn-primary btn-block" (click)="close()">Закрыть приложение</button>
      </div>
    </div>
  `,
})
export class TelegramExpiredComponent {
  private readonly tg = inject(TelegramService);

  close(): void {
    this.tg.close();
  }
}
