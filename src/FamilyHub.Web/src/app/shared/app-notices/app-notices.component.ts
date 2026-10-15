import { Component, DestroyRef, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { SwUpdate } from '@angular/service-worker';
import { CircleArrowUp, Share, Smartphone, WifiOff } from 'lucide';
import { IconComponent } from '../ui/icon/icon.component';

const IOS_HINT_DISMISSED_KEY = 'familyhub.iosInstallHintDismissed';

function isIos(): boolean {
  const ua = navigator.userAgent;
  return /iPad|iPhone|iPod/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
}

function isStandalone(): boolean {
  return window.matchMedia?.('(display-mode: standalone)').matches
    || (navigator as Navigator & { standalone?: boolean }).standalone === true;
}

function readFlag(key: string): boolean {
  try { return localStorage.getItem(key) === '1'; } catch { return false; }
}

/**
 * Служебные плашки каркаса PWA:
 * - нет интернета — раньше каждый запрос молча падал с непонятной ошибкой;
 * - вышла новая версия — раньше пользователь сидел на старой, пока сам не перезагрузит;
 * - iPhone в Safari — без установки на «Домой» не будет уведомлений, а подсказки не было нигде.
 */
@Component({
  selector: 'app-notices',
  imports: [IconComponent],
  standalone: true,
  template: `
    @if (offline()) {
      <div class="app-notice app-notice-warn" role="status">
        <app-icon [icon]="icons.WifiOff" [size]="18" />
        <span class="flex-fill">Нет интернета. Показываем то, что уже загружено, но новые изменения не сохранятся, пока связь не вернётся.</span>
      </div>
    }
    @if (updateReady()) {
      <div class="app-notice" role="status">
        <app-icon [icon]="icons.CircleArrowUp" [size]="18" />
        <span class="flex-fill">Вышла новая версия FamilyHub.</span>
        <button type="button" class="btn btn-primary btn-sm" (click)="reload()">Обновить</button>
      </div>
    }
    @if (showIosHint()) {
      <div class="app-notice" role="note">
        <app-icon [icon]="icons.Smartphone" [size]="18" />
        <span class="flex-fill">
          Установите FamilyHub на iPhone: нажмите «Поделиться» <app-icon [icon]="icons.Share" [size]="18" />
          внизу Safari → «На экран «Домой»». Так приложение откроется одним касанием и сможет присылать
          напоминания о лекарствах.
        </span>
        <button type="button" class="btn btn-secondary btn-sm" (click)="dismissIosHint()">Понятно</button>
      </div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: `
    .app-notice {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      flex-wrap: wrap;
      margin-bottom: var(--space-2);
      padding: var(--space-3) var(--space-4);
      background: var(--color-accent-100);
      color: var(--color-accent-800);
      border-radius: var(--radius-md);
      font-size: 0.8824rem;

      > app-icon { flex: none; }
    }
    .app-notice-warn {
      background: color-mix(in srgb, var(--color-status-warning) 14%, var(--color-paper));
      color: var(--color-status-warning-text);
    }
  `,
})
export class AppNoticesComponent {
  protected readonly icons = { CircleArrowUp, Share, Smartphone, WifiOff };

  private readonly sw = inject(SwUpdate);

  readonly offline = signal(typeof navigator !== 'undefined' && navigator.onLine === false);
  readonly updateReady = signal(false);
  readonly showIosHint = signal(isIos() && !isStandalone() && !readFlag(IOS_HINT_DISMISSED_KEY));

  constructor() {
    const online = () => this.offline.set(false);
    const offline = () => this.offline.set(true);
    window.addEventListener('online', online);
    window.addEventListener('offline', offline);
    inject(DestroyRef).onDestroy(() => {
      window.removeEventListener('online', online);
      window.removeEventListener('offline', offline);
    });

    if (this.sw.isEnabled) {
      this.sw.versionUpdates.subscribe((e) => {
        if (e.type === 'VERSION_READY') this.updateReady.set(true);
      });
      // Кэш сломан (редко, но бывает после крупных обновлений) — без перезагрузки приложение не оживёт.
      this.sw.unrecoverable.subscribe(() => this.updateReady.set(true));
    }
  }

  reload(): void {
    document.location.reload();
  }

  dismissIosHint(): void {
    this.showIosHint.set(false);
    try { localStorage.setItem(IOS_HINT_DISMISSED_KEY, '1'); } catch { /* приватный режим — просто не запомним */ }
  }
}
