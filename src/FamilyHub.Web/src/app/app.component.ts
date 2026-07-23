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
const AUTH_ROUTE_PREFIXES = ['/login', '/consent'];

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

  readonly tabs: { id: string; label: string; icon: string }[] = [
    { id: 'families', label: 'Семьи', icon: 'ph-users-three' },
    { id: 'medications', label: 'Аптечка', icon: 'ph-first-aid-kit' },
    { id: 'birthdays', label: 'Дни р.', icon: 'ph-cake' },
    { id: 'records', label: 'Анализы', icon: 'ph-heartbeat' },
    { id: 'notifications', label: 'Оповещ.', icon: 'ph-bell' },
    { id: 'settings', label: 'Ещё', icon: 'ph-gear' },
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
    // последующие login()/registerConfirm()/resetPinConfirm() в той же SPA-сессии (без
    // перезагрузки страницы) никогда не triggerили refresh(): state.loading оставался true
    // навсегда, и глобальный спиннер оболочки зависал на любом экране после входа.
    // Telegram-режим эффектом не покрываем — там refresh() уже вызывается немедленно в
    // ngOnInit, не дожидаясь /me (см. ниже); иначе тут случился бы двойной вызов.
    // refresh() пишет в свои families/loading/error-сигналы уже после await (в микротаске) —
    // zone.js сохраняет реактивный контекст эффекта поперёк await, поэтому Angular всё равно
    // требует allowSignalWrites для этого пути. Циклической реактивности здесь нет: сигналы
    // FamilyStateService не влияют на условие эффекта (auth.mode/auth.me()).
    effect(() => {
      if (this.auth.mode === 'pwa' && this.auth.me() !== null) {
        this.state.refresh();
      }
    }, { allowSignalWrites: true });
  }

  ngOnInit(): void {
    this.tg.init();
    this.subscribeToRouter();
    this.initAuth();
  }

  private initAuth(): void {
    if (this.auth.mode === 'telegram') {
      // Telegram-сессия аутентифицирована неявно (заголовок на каждый запрос) — семьи
      // грузим сразу же, НЕ дожидаясь /me: единственная задержка/транзиентная ошибка ЭТОГО
      // одного запроса (а не самих семейных данных) иначе оставляла бы state.loading true
      // навсегда. loadMe() всё равно запускаем — для профиля/username в настройках, — но
      // параллельно, не блокируя ничего.
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
