import { Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { Router, RouterOutlet, RouterLink, RouterLinkActive, NavigationStart, NavigationEnd, NavigationError } from '@angular/router';
import { TelegramService } from './services/telegram.service';
import { FamilyStateService } from './services/family-state.service';
import { AuthService } from './services/auth.service';
import { DevLoggerService } from './services/dev-logger.service';
import { DevPanelComponent } from './components/dev-panel/dev-panel.component';
import { ToastContainerComponent } from './shared/toast/toast-container.component';
import { ConfirmDialogComponent } from './shared/confirm/confirm-dialog.component';
import { LoadingSpinnerComponent } from './shared/loading-spinner/loading-spinner.component';
import { CookieBannerComponent } from './shared/cookie-banner/cookie-banner.component';

/** Маршруты без хедера/навигации приложения — вход и согласие ПДн показываются как отдельный экран. */
const AUTH_ROUTE_PREFIXES = ['/login', '/consent', '/telegram-bind'];

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    DevPanelComponent,
    ToastContainerComponent,
    ConfirmDialogComponent,
    LoadingSpinnerComponent,
    CookieBannerComponent,
  ],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
  readonly state = inject(FamilyStateService);
  readonly auth = inject(AuthService);
  private readonly tg = inject(TelegramService);
  private readonly router = inject(Router);
  private readonly log = inject(DevLoggerService);

  // Редизайн навигации: 7 табов → 4 (Поиск — иконка в шапке, см. app.component.html; Аптечка +
  // Анализы объединены в хаб «Здоровье», /health/*; Дни рождения — виджет на Главной + отдельная
  // страница /birthdays без своего таба, см. FamiliesTabComponent).
  readonly tabs: { id: string; label: string; icon: string }[] = [
    { id: 'families', label: 'Главная', icon: 'ph-users-three' },
    { id: 'health', label: 'Здоровье', icon: 'ph-heartbeat' },
    { id: 'notifications', label: 'Уведомл.', icon: 'ph-bell' },
    { id: 'settings', label: 'Профиль', icon: 'ph-user' },
  ];

  /** Консервативный дефолт — скрыто, пока первая навигация не подтвердит обратное (без мигания). */
  private readonly onAuthRoute = signal(this.isAuthRoute(this.router.url));

  /**
   * Таб-бар показывается только вошедшему пользователю и не на экранах входа/согласия.
   * В Telegram-режиме аутентификация неявная (заголовок на каждый запрос, см. authGuard,
   * который в этом режиме пропускает без обращения к /me) — поэтому здесь НЕЛЬЗЯ ждать
   * auth.me(): она заполняется только отдельным async-вызовом loadMe() ниже, и до его
   * завершения (или при любой транзиентной ошибке этого запроса) нав-бар просто не появился
   * бы, хотя пользователь уже полноценно аутентифицирован самим фактом работы внутри Mini App.
   * В PWA-режиме, наоборот, me() — единственный источник истины о входе.
   */
  readonly showTabs = computed(() =>
    !this.onAuthRoute() && (this.auth.mode === 'telegram' || this.auth.me() !== null));

  constructor() {
    // PWA: реагируем на КАЖДЫЙ переход auth.me() в непустое состояние, а не только на
    // бутстрап приложения. Раньше refresh() запускался один раз в ngOnInit — если в этот
    // момент пользователь ещё не был аутентифицирован (например, только что открыл /login),
    // последующие login()/registerConfirm()/resetPasswordConfirm() в той же SPA-сессии (без
    // перезагрузки страницы) никогда не triggerили refresh(): state.loading оставался true
    // навсегда, и глобальный спиннер оболочки зависал на любом экране после входа.
    // Telegram-режим покрывается отдельным эффектом ниже (по telegramBound(), а не по me()).
    // refresh() пишет в свои families/loading/error-сигналы уже после await (в микротаске) —
    // zone.js сохраняет реактивный контекст эффекта поперёк await, поэтому Angular всё равно
    // требует allowSignalWrites для этого пути. Циклической реактивности здесь нет: сигналы
    // FamilyStateService не влияют на условие эффекта (auth.mode/auth.me()).
    effect(() => {
      if (this.auth.mode === 'pwa' && this.auth.me() !== null) {
        this.state.refresh();
      }
    }, { allowSignalWrites: true });

    // Telegram: тот же принцип, что и выше для PWA, но по переходу telegramBound() в true —
    // не только на бутстрапе (initAuth ниже), а на ЛЮБОМ таком переходе, включая более поздний,
    // случившийся уже после первого рендера (успешная привязка через TelegramBindComponent).
    // Раньше initAuth() грузил семьи/профиль напрямую и только на бутстрапе; если на тот момент
    // TelegramId ещё не был привязан, initAuth() выходил раньше этого вызова и никогда больше не
    // запускался повторно (ngOnInit — один раз) — после последующей привязки и навигации на "/"
    // ничего не догружало ни /me, ни семьи, и FamilyStateService.loading оставался true навсегда,
    // из-за чего зависимые панели/страницы вечно показывали спиннер, хотя пользователь уже вошёл.
    effect(() => {
      if (this.auth.mode === 'telegram' && this.auth.telegramBound() === true) {
        this.state.refresh();
        void this.auth.loadMe();
      }
    }, { allowSignalWrites: true });
  }

  ngOnInit(): void {
    this.tg.init();
    this.subscribeToRouter();
    void this.initAuth();
  }

  private async initAuth(): Promise<void> {
    if (this.auth.mode === 'telegram') {
      // Реальный Telegram Mini App: TelegramId может быть ещё не привязан ни к одному
      // аккаунту (TelegramMiniAppAuthenticationHandler — lookup-only, отклонит любой запрос
      // до привязки). Дожидаемся результата — грузить семьи/профиль отсюда напрямую не
      // нужно: telegramBound() пишется внутри ensureTelegramBound() в любом случае (true
      // и false), и эффект в конструкторе сам среагирует на переход в true. Именно поэтому
      // тем же эффектом (а не отдельным вызовом здесь) покрывается и случай более ПОЗДНЕЙ
      // привязки через TelegramBindComponent — тогда initAuth() уже не выполняется повторно
      // (ngOnInit — один раз), и без общего эффекта грузить семьи/профиль было бы некому.
      if (this.tg.isInsideTelegram()) {
        await this.auth.ensureTelegramBound();
        return;
      }

      // Dev-заголовок (X-Dev-TelegramId): DevAuthenticationHandler авто-создаёт пользователя,
      // привязка не нужна — telegramBound() для этого пути остаётся null навсегда, эффект
      // выше никогда не сработает, поэтому грузим сразу же явно.
      void this.auth.loadMe();
      this.state.refresh();
      return;
    }

    // PWA: без cookie-сессии /api/families ответит 401 — сначала убеждаемся, что вошли,
    // иначе неаутентифицированный пользователь на /login увидел бы лишний баннер ошибки.
    // Сам refresh() при успехе — забота эффекта в конструкторе (сработает и сейчас, если уже
    // была валидная cookie-сессия, и позже — после интерактивного входа/регистрации).
    void this.auth.loadMe();
  }

  private isAuthRoute(url: string): boolean {
    return AUTH_ROUTE_PREFIXES.some((prefix) => url.startsWith(prefix));
  }

  private subscribeToRouter(): void {
    this.router.events.subscribe((e) => {
      if (e instanceof NavigationStart) {
        this.log.log('nav', 'info', `→ ${e.url}`);
      } else if (e instanceof NavigationEnd) {
        this.onAuthRoute.set(this.isAuthRoute(e.urlAfterRedirects));
        this.log.log('nav', 'info', `✓ ${e.urlAfterRedirects}`);
      } else if (e instanceof NavigationError) {
        this.log.log('nav', 'error', `✗ ${e.url}: ${String(e.error)}`);
      }
    });
  }
}
