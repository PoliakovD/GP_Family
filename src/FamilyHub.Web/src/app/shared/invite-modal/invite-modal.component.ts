import { Component, inject, input, output, signal } from '@angular/core';
import { ApiService, ApiError } from '../../services/api.service';
import { TelegramService } from '../../services/telegram.service';
import type { InviteCreated } from '../../models/types';
import { ToastService } from '../toast/toast.service';
import { ModalComponent } from '../modal/modal.component';
import { copyToClipboard } from '../util/clipboard';

/**
 * Модалка «Пригласить в семью» — создание одноразовой ссылки-приглашения и её шаринг. Вынесена из
 * family-details, чтобы тот же поток открывался прямо с Главной («Кто в семье» → «Пригласить по
 * ссылке»). Создавать инвайт может только админ семьи (проверяет бэк) — скрывать вход для не-админов
 * должен вызывающий.
 */
@Component({
  selector: 'app-invite-modal',
  standalone: true,
  imports: [ModalComponent],
  template: `
    <app-modal title="Пригласить в семью" [open]="open()" (closed)="closed.emit()">
      @if (created(); as invite) {
        <p class="muted mb-2">
          Код: <code>{{ invite.code }}</code>
        </p>
        <div class="invite-link mb-2">{{ invite.webLink }}</div>
        <div class="d-flex gap-2 mb-2">
          <button class="btn btn-primary flex-grow-1" (click)="share(invite.webLink)">Поделиться ссылкой</button>
          <button
            type="button"
            class="btn btn-secondary btn-icon"
            title="Скопировать ссылку"
            aria-label="Скопировать ссылку"
            (click)="copy(invite.webLink)"
          >
            <i class="ph ph-copy" aria-hidden="true"></i>
          </button>
          @if (invite.telegramLink) {
            <button
              type="button"
              class="btn btn-secondary btn-icon"
              title="Отправить в Telegram"
              aria-label="Отправить в Telegram"
              (click)="openTelegram(invite.telegramLink)"
            >
              <i class="ph-fill ph-telegram-logo" aria-hidden="true"></i>
            </button>
          } @else {
            <p class="muted mb-0 align-self-center">
              Или боту: <code>/start {{ invite.code }}</code>
            </p>
          }
        </div>
        <div class="d-flex justify-content-end gap-2">
          <button class="btn btn-secondary btn-sm" (click)="closed.emit()">Закрыть</button>
          <button class="btn btn-secondary btn-sm" [disabled]="creating()" (click)="create()">Создать ещё одну</button>
        </div>
      } @else {
        <p class="muted mb-3">
          Ссылка-приглашение будет действовать одноразово. Отправьте её тому, кого хотите позвать — после
          перехода по ней человек попадёт в список заявок на вступление.
        </p>
        <div class="d-flex justify-content-end gap-2">
          <button class="btn btn-secondary btn-sm" (click)="closed.emit()">Отмена</button>
          <button class="btn btn-primary btn-sm" [disabled]="creating()" (click)="create()">Создать ссылку</button>
        </div>
      }
    </app-modal>
  `,
  styles: `
    // Саму ссылку показываем текстом: на десктопе с navigator.share пользователь иначе никогда
    // не получил бы её для копирования вручную.
    .invite-link {
      padding: 8px 12px;
      background: var(--color-paper);
      border: 1px solid var(--color-divider);
      border-radius: var(--radius-md);
      font-family: monospace;
      font-size: var(--font-size-meta);
      word-break: break-all;
      user-select: all;
    }
  `,
})
export class InviteModalComponent {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly tg = inject(TelegramService);

  readonly familyId = input.required<string>();
  readonly open = input(false);
  readonly closed = output<void>();

  readonly created = signal<InviteCreated | null>(null);
  readonly creating = signal(false);

  async create(): Promise<void> {
    this.creating.set(true);
    try {
      this.created.set(await this.api.createInvite(this.familyId()));
      this.toast.success('Инвайт создан.');
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось создать инвайт.');
    } finally {
      this.creating.set(false);
    }
  }

  /** Кнопка-самолётик — открывает бот-диплинк инвайта напрямую, в отличие от share() ниже
   * (который открывает системный шаринг ссылки, а не саму Telegram-ссылку). */
  openTelegram(telegramLink: string): void {
    this.tg.openTelegramLink(telegramLink);
  }

  async share(link: string): Promise<void> {
    // Внутри Telegram — открываем нативный шаринг (пользователь сам выбирает контакт/чат
    // из списка Telegram; мы не запрашиваем и не храним чужие Telegram ID).
    if (this.tg.isInsideTelegram()) {
      const shareUrl = `https://t.me/share/url?url=${encodeURIComponent(link)}&text=${encodeURIComponent('Присоединяйтесь к нашей семье в FamilyHub')}`;
      this.tg.openTelegramLink(shareUrl);
      return;
    }

    if (navigator.share) {
      try {
        await navigator.share({
          title: 'Приглашение в семью FamilyHub',
          text: 'Присоединяйтесь к нашей семье в FamilyHub',
          url: link,
        });
      } catch {
        // пользователь отменил диалог — игнорируем
      }
    } else {
      await this.copy(link);
    }
  }

  /** Отдельная «Скопировать» рядом с «Поделиться»: на десктопе с navigator.share (Chrome/Edge)
   * пользователь всегда получает системный шер-лист и никогда не попадает в clipboard-ветку. */
  async copy(link: string): Promise<void> {
    const ok = await copyToClipboard(link);
    if (ok) {
      this.toast.success('Ссылка скопирована в буфер обмена.');
    } else {
      this.toast.error('Не удалось скопировать — выделите ссылку вручную.');
    }
  }
}
