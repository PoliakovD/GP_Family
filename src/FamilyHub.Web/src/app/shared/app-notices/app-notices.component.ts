import { Component, DestroyRef, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { SwUpdate } from '@angular/service-worker';
import { CircleArrowUp, Share, Smartphone, WifiOff } from 'lucide';
import { IconComponent } from '../ui/icon/icon.component';
import { AuthService } from '../../services/auth.service';

const IOS_HINT_DISMISSED_KEY = 'familyhub.iosInstallHintDismissed';

/**
 * Сколько ждём тишины после последнего действия пользователя, прежде чем перезагрузиться ради
 * новой версии. Короче — рискуем прервать чтение/ввод, дольше — обновление «зависает» надолго.
 */
const AUTO_RELOAD_IDLE_MS = 90_000;
/** Хэш версии, ради которой уже перезагружались: второй раз не перезагружаемся, а показываем плашку. */
const AUTO_RELOAD_HASH_KEY = 'familyhub.swAutoReloadHash';
/**
 * Пока идёт POST /api/auth/refresh, перезагружаться нельзя: reload оборвёт ротацию refresh-токена,
 * браузер останется со старой cookie (бэкенд уже её погасил), и следующий refresh выглядит как
 * reuse-detection — он отзывает ВСЕ сессии пользователя. Ждём короткими шагами.
 */
const REFRESH_SETTLE_RETRY_MS = 2_000;
/** Предел ожидания: зависший refresh не должен навсегда блокировать обновление приложения. */
const MAX_REFRESH_DEFERRALS = 15;
/**
 * Сломанный кэш: если перезагрузка не помогла и состояние повторилось раньше, чем через минуту,
 * значит мы попали бы в цикл — показываем плашку вместо ещё одной перезагрузки.
 */
const UNRECOVERABLE_KEY = 'familyhub.swUnrecoverableAt';
const UNRECOVERABLE_GUARD_MS = 60_000;

/** Действия, после которых отсчёт «тишины» начинается заново. */
const ACTIVITY_EVENTS = ['pointerdown', 'keydown', 'touchstart', 'wheel'] as const;

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

function readSession(key: string): string | null {
  try { return sessionStorage.getItem(key); } catch { return null; }
}

function writeSession(key: string, value: string): void {
  try { sessionStorage.setItem(key, value); } catch { /* приватный режим — просто не запомним */ }
}

/**
 * Есть ли что потерять при перезагрузке: печатают в поле/контент-едитабле или в форме остались
 * изменённые поля. В таком случае обновление не применяем молча — отдаём решение пользователю.
 */
function hasUnsavedWork(): boolean {
  const active = document.activeElement as HTMLElement | null;
  if (active) {
    const tag = active.tagName;
    if (active.isContentEditable || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true;
  }
  return document.querySelector('form.ng-dirty') !== null;
}

/**
 * Служебные плашки каркаса PWA:
 * - нет интернета — раньше каждый запрос молча падал с непонятной ошибкой;
 * - вышла новая версия — применяем сами: без клика по кнопке, но в безопасный момент;
 * - iPhone в Safari — без установки на «Домой» не будет уведомлений, а подсказки не было нигде.
 *
 * Автообновление: как только SW скачал новую версию, страница перезагружается сама — сразу, если
 * вкладка скрыта, иначе после 90 секунд без действий. Пока идёт ротация refresh-токена
 * (AuthService.refreshInFlight), перезагрузка откладывается: иначе она оборвала бы refresh, и
 * следующий запрос выглядел бы как reuse-detection с отзывом всех сессий.
 * Плашка остаётся запасным вариантом: есть несохранённый ввод, либо перезагрузка уже не помогла
 * (защита от цикла).
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
    @if (updateFailed()) {
      <div class="app-notice app-notice-warn" role="status">
        <app-icon [icon]="icons.CircleArrowUp" [size]="18" />
        <span class="flex-fill">Не удалось применить обновление автоматически. Перезагрузите приложение.</span>
        <button type="button" class="btn btn-primary btn-sm" (click)="reload()">Перезагрузить</button>
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
      background: var(--color-accent-soft);
      color: var(--color-accent-hover);
      border-radius: var(--radius-md);
      font: var(--text-body-sm);

      > app-icon { flex: none; }
    }
    .app-notice-warn {
      background: color-mix(in srgb, var(--color-warning) 14%, var(--color-surface));
      color: var(--color-warning);
    }
  `,
})
export class AppNoticesComponent {
  protected readonly icons = { CircleArrowUp, Share, Smartphone, WifiOff };

  private readonly sw = inject(SwUpdate);
  private readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);

  /** Скачанная версия ждёт безопасного момента для перезагрузки. */
  private pendingUpdate = false;
  private idleTimer: ReturnType<typeof setTimeout> | null = null;

  readonly offline = signal(typeof navigator !== 'undefined' && navigator.onLine === false);
  readonly updateReady = signal(false);
  readonly updateFailed = signal(false);
  readonly showIosHint = signal(isIos() && !isStandalone() && !readFlag(IOS_HINT_DISMISSED_KEY));

  constructor() {
    const online = () => this.offline.set(false);
    const offline = () => this.offline.set(true);
    window.addEventListener('online', online);
    window.addEventListener('offline', offline);
    for (const event of ACTIVITY_EVENTS) window.addEventListener(event, this.onActivity, { passive: true });
    document.addEventListener('visibilitychange', this.onVisibilityChange);

    if (this.sw.isEnabled) {
      this.sw.versionUpdates.subscribe((e) => {
        if (e.type === 'VERSION_READY') this.onVersionReady(e.latestVersion.hash);
      });
      this.sw.unrecoverable.subscribe(() => this.onUnrecoverable());
    }

    this.destroyRef.onDestroy(() => {
      window.removeEventListener('online', online);
      window.removeEventListener('offline', offline);
      for (const event of ACTIVITY_EVENTS) window.removeEventListener(event, this.onActivity);
      document.removeEventListener('visibilitychange', this.onVisibilityChange);
      this.clearIdleTimer();
    });
  }

  reload(): void {
    document.location.reload();
  }

  dismissIosHint(): void {
    this.showIosHint.set(false);
    try { localStorage.setItem(IOS_HINT_DISMISSED_KEY, '1'); } catch { /* приватный режим — просто не запомним */ }
  }

  /** Новая версия скачана: применяем сами, без клика по кнопке. */
  private onVersionReady(hash: string): void {
    if (readSession(AUTO_RELOAD_HASH_KEY) === hash) {
      // Ради этой версии уже перезагружались, но она снова «готова» — в цикл не уходим.
      this.updateReady.set(true);
      return;
    }
    writeSession(AUTO_RELOAD_HASH_KEY, hash);
    this.pendingUpdate = true;

    if (document.visibilityState === 'hidden') {
      // Вкладка не на экране — перезагрузка никого не прервёт.
      void this.applyPendingUpdate();
      return;
    }
    this.restartIdleTimer();
  }

  /** Кэш SW сломан: без перезагрузки приложение не оживёт. */
  private onUnrecoverable(): void {
    const last = Number(readSession(UNRECOVERABLE_KEY) ?? 0);
    if (Date.now() - last < UNRECOVERABLE_GUARD_MS) {
      // Перезагрузка только что была и не помогла — дальше решает пользователь.
      this.updateFailed.set(true);
      return;
    }
    writeSession(UNRECOVERABLE_KEY, String(Date.now()));
    this.pendingUpdate = false;
    this.clearIdleTimer();
    this.reload();
  }

  private async applyPendingUpdate(refreshDeferrals = 0): Promise<void> {
    if (!this.pendingUpdate) return;
    this.clearIdleTimer();

    if (this.auth.refreshInFlight() && refreshDeferrals < MAX_REFRESH_DEFERRALS) {
      // Ротация refresh-токена в полёте — ждём её завершения, не сбрасывая pendingUpdate.
      this.idleTimer = setTimeout(
        () => void this.applyPendingUpdate(refreshDeferrals + 1), REFRESH_SETTLE_RETRY_MS);
      return;
    }

    if (hasUnsavedWork()) {
      // Есть что потерять — молча не перезагружаемся, показываем привычную кнопку.
      this.pendingUpdate = false;
      this.updateReady.set(true);
      return;
    }

    this.pendingUpdate = false;
    try {
      // activateUpdate переключает текущую страницу на новую версию, поэтому reload идёт сразу за ним:
      // иначе ленивые чанки старой сборки могут уже не найтись.
      await this.sw.activateUpdate();
    } catch {
      // Активация могла не пройти (SW перезапустился) — обычная перезагрузка всё равно подхватит новую версию.
    }
    this.reload();
  }

  private restartIdleTimer(): void {
    this.clearIdleTimer();
    this.idleTimer = setTimeout(() => void this.applyPendingUpdate(), AUTO_RELOAD_IDLE_MS);
  }

  private clearIdleTimer(): void {
    if (this.idleTimer === null) return;
    clearTimeout(this.idleTimer);
    this.idleTimer = null;
  }

  /** Действие пользователя — отсчитываем «тишину» заново. */
  private readonly onActivity = (): void => {
    if (this.idleTimer !== null) this.restartIdleTimer();
  };

  /** Вкладку свернули — самый безопасный момент: при возврате человек сразу увидит новую версию. */
  private readonly onVisibilityChange = (): void => {
    if (document.visibilityState === 'hidden' && this.idleTimer !== null) void this.applyPendingUpdate();
  };
}
