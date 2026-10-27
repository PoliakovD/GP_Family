import { Component, inject, ChangeDetectionStrategy } from '@angular/core';
import { TelegramService } from '../../services/telegram.service';
import { AuthCardComponent } from '../../shared/ui/auth-card/auth-card.component';

/**
 * Mini App открыт дольше, чем живёт его initData (Telegram:MaxInitDataAge, 1 ч — аудит
 * security-audit-2026-10, M6): Telegram выдаёт свежие данные только при новом открытии, поэтому
 * единственное действие — закрыть и открыть приложение заново. Раньше такой 401 уводил на экран
 * привязки почты, будто аккаунт отвязан.
 */
@Component({
  selector: 'app-telegram-expired',
  imports: [AuthCardComponent],
  changeDetection: ChangeDetectionStrategy.Eager,
  template: `
    <app-auth-card
      title="Сессия устарела"
      lead="Приложение было открыто слишком долго. Для защиты ваших данных закройте его и откройте заново из чата с ботом — все данные на месте.">
      <button type="button" class="btn btn-primary btn-block" (click)="close()">Закрыть приложение</button>
    </app-auth-card>
  `,
})
export class TelegramExpiredComponent {
  private readonly tg = inject(TelegramService);

  close(): void {
    this.tg.close();
  }
}
