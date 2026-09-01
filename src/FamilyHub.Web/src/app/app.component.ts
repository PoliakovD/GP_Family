import { Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { Router, RouterOutlet, RouterLink, NavigationStart, NavigationEnd, NavigationError } from '@angular/router';
import { TelegramService } from './services/telegram.service';
import { FamilyStateService } from './services/family-state.service';
import { NotificationStateService } from './services/notification-state.service';
import { PageActionService } from './services/page-action.service';
import { BreakpointService } from './services/breakpoint.service';
import { AuthService } from './services/auth.service';
import { DevLoggerService } from './services/dev-logger.service';
import { ApiError, ApiService } from './services/api.service';
import { PendingInviteService } from './services/pending-invite.service';
import { ToastService } from './shared/toast/toast.service';
import { DevPanelComponent } from './components/dev-panel/dev-panel.component';
import { ToastContainerComponent } from './shared/toast/toast-container.component';
import { ConfirmDialogComponent } from './shared/confirm/confirm-dialog.component';
import { LoadingSpinnerComponent } from './shared/loading-spinner/loading-spinner.component';
import { CookieBannerComponent } from './shared/cookie-banner/cookie-banner.component';
import { BottomSheetComponent } from './shared/bottom-sheet/bottom-sheet.component';
import { AvatarComponent } from './shared/avatar/avatar.component';
import { AppSearchComponent } from './components/app-search/app-search.component';

/** Маршруты без хедера/навигации приложения — вход и согласие ПДн показываются как отдельный экран.
 * /admin — отдельная поверхность (ADR-0009), никогда не показывает обычный таб-бар приложения. */
const AUTH_ROUTE_PREFIXES = ['/login', '/consent', '/telegram-bind', '/admin'];

/** Один пункт бокового меню (десктоп, ≥1024px) — редизайн v2, каркас навигации (PR2). Подпункты —
 * уже существующие роуты (Здоровье) или query-параметр на уже существующем роуте (Семья, см.
 * FamilyDetailsComponent.selectSubTab) — новых роутов/разделов не заводится. */
interface SidebarItem {
  id: string;
  label: string;
  icon: string;
  /** Абсолютный путь; для «Семьи» вычисляется динамически (см. familyHref()). */
  path?: string;
  children?: { path: string; label: string; queryParams?: Record<string, string> }[];
}

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [
    RouterOutlet,
    RouterLink,
    DevPanelComponent,
    ToastContainerComponent,
    ConfirmDialogComponent,
    LoadingSpinnerComponent,
    CookieBannerComponent,
    BottomSheetComponent,
    AvatarComponent,
    AppSearchComponent,
  ],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
  readonly state = inject(FamilyStateService);
  readonly auth = inject(AuthService);
  readonly notifications = inject(NotificationStateService);
  readonly pageAction = inject(PageActionService);
  private readonly breakpoints = inject(BreakpointService);
  private readonly tg = inject(TelegramService);
  private readonly router = inject(Router);
  private readonly log = inject(DevLoggerService);
  private readonly api = inject(ApiService);
  private readonly pendingInvite = inject(PendingInviteService);
  private readonly toast = inject(ToastService);

  /** Признак десктопа — существующий BreakpointService (первая брейкпойнт-абстракция в
   * проекте), второй параллельный механизм не заводим (см. .claude/patterns/frontend_web.md). */
  readonly isWide = computed(() => this.breakpoints.tier() === 'wide');

  readonly moreSheetOpen = signal(false);
  readonly familySwitcherOpen = signal(false);

  // Активность пунктов навигации считается явно от текущего URL, а не через директиву
  // routerLinkActive — «Семья» ведёт на динамический /families/{id} с ?tab=, для неё
  // routerLinkActive не годится в принципе (сравнивать пришлось бы с постоянно меняющейся
  // ссылкой), поэтому для единообразия все пункты каркаса считаются одним и тем же способом.
  // Публичный (не private) — читается из шаблона (strictTemplates не пропустит private-поле).
  readonly currentUrl = signal(this.router.url);

  readonly isHomeActive = computed(() => this.currentUrl().startsWith('/home'));
  readonly isHealthActive = computed(() => this.currentUrl().startsWith('/health'));
  readonly isFamilyActive = computed(() => this.currentUrl().startsWith('/families'));
  readonly isNotificationsActive = computed(() => this.currentUrl().startsWith('/notifications'));
  readonly isSettingsActive = computed(() => this.currentUrl().startsWith('/settings'));

  /** Здоровье → существующие вложенные роуты health-hub. Семья → тот же /families/:id, что и
   * обычный переход, но с ?tab= на нужный саб-таб (FamilyDetailsComponent, см. PR2). */
  readonly sidebarItems: SidebarItem[] = [
    { id: 'home', label: 'Главная', icon: 'ph-house', path: '/home' },
    {
      id: 'health',
      label: 'Здоровье',
      icon: 'ph-heartbeat',
      path: '/health',
      children: [
        { path: '/health/medications', label: 'Аптечка' },
        { path: '/health/records', label: 'Анализы' },
        { path: '/health/visits', label: 'Врачи' },
        { path: '/health/kb', label: 'Справочник' },
        { path: '/health/indicators', label: 'Показатели' },
      ],
    },
    { id: 'family', label: 'Семья', icon: 'ph-users-three' }, // path вычисляется — см. familyHref()
    { id: 'notifications', label: 'Уведомления', icon: 'ph-bell', path: '/notifications' },
    { id: 'settings', label: 'Профиль', icon: 'ph-user', path: '/settings' },
  ];

  /** Семейные саб-пункты — фиксированные ярлыки, роут всегда на текущую выбранную семью с ?tab=. */
  readonly familySubItems: { tab: string; label: string }[] = [
    { tab: 'members', label: 'Участники' },
    { tab: 'medkits', label: 'Аптечки' },
    { tab: 'birthdays', label: 'Дни рождения' },
    { tab: 'dependents', label: 'Близкие и питомцы' },
  ];

  /** Куда ведёт пункт «Семья» — решённый вопрос №1 плана редизайна: сразу в текущую выбранную
   * семью, а не на список. У нового пользователя без семей активных семей ещё нет — тогда
   * единственный осмысленный переход — список/создание. */
  familyHref(): string {
    const family = this.state.selectedFamily();
    return family ? `/families/${family.id}` : '/families';
  }

  isSidebarItemActive(id: string): boolean {
    switch (id) {
      case 'home': return this.isHomeActive();
      case 'health': return this.isHealthActive();
      case 'family': return this.isFamilyActive();
      case 'notifications': return this.isNotificationsActive();
      case 'settings': return this.isSettingsActive();
      default: return false;
    }
  }

  toggleFamilySwitcher(): void {
    this.familySwitcherOpen.update((v) => !v);
  }

  selectFamilyAndClose(id: string): void {
    this.state.selectFamily(id);
    this.familySwitcherOpen.set(false);
  }

  /** Консервативный дефолт — скрыто, пока первая навигация не подтвердит обратное (без мигания). */
  private readonly onAuthRoute = signal(this.isAuthRoute(this.router.url));

  /**
   * Таб-бар показывается только вошедшему пользователю и не на экранах входа/согласия.
   * В Telegram-режиме аутентификация неявная (заголовок на каждый запрос, см. authGuard, который
   * в этом режиме пропускает без обращения к /me) — поэтому здесь НЕЛЬЗЯ ждать auth.me(): она
   * заполняется только отдельным async-вызовом loadMe() ниже, и до его завершения (или при любой
   * транзиентной ошибке этого запроса) нав-бар просто не появился бы, хотя пользователь уже
   * полноценно аутентифицирован самим фактом работы внутри Mini App.
   * В PWA-режиме, наоборот, me() — единственный источник истины о входе.
   */
  readonly showTabs = computed(() =>
    !this.onAuthRoute() && (this.auth.mode === 'telegram' || this.auth.me() !== null));

  constructor() {
    // PWA: реагируем на КАЖДЫЙ переход auth.me() в непустое состояние, а не только на бутстрап
    // приложения. Раньше refresh() запускался один раз в ngOnInit — если в этот момент
    // пользователь ещё не был аутентифицирован (например, только что открыл /login), последующие
    // login()/registerConfirm()/resetPasswordConfirm() в той же SPA-сессии (без перезагрузки
    // страницы) никогда не triggerили refresh(): state.loading оставался true навсегда, и
    // глобальный спиннер оболочки зависал на любом экране после входа. Telegram-режим покрывается
    // отдельным эффектом ниже (по telegramBound(), а не по me()). refresh() пишет в свои
    // families/loading/error-сигналы уже после await (в микротаске) — zone.js сохраняет
    // реактивный контекст эффекта поперёк await, поэтому Angular всё равно требует
    // allowSignalWrites для этого пути. Циклической реактивности здесь нет: сигналы
    // FamilyStateService не влияют на условие эффекта (auth.mode/auth.me()).
    effect(() => {
      if (this.auth.mode === 'pwa' && this.auth.me() !== null) {
        this.state.refresh();
        void this.notifications.refresh();
        void this.tryRedeemPendingInvite();
      }
    }, { allowSignalWrites: true });

    // Telegram: тот же принцип, что и выше для PWA, но по переходу telegramBound() в true — не
    // только на бутстрапе (initAuth ниже), а на ЛЮБОМ таком переходе, включая более поздний,
    // случившийся уже после первого рендера (успешная привязка через TelegramBindComponent).
    // Раньше initAuth() грузил семьи/профиль напрямую и только на бутстрапе; если на тот момент
    // TelegramId ещё не был привязан, initAuth() выходил раньше этого вызова и никогда больше не
    // запускался повторно (ngOnInit — один раз) — после последующей привязки и навигации на "/"
    // ничего не догружало ни /me, ни семьи, и FamilyStateService.loading оставался true навсегда,
    // из-за чего зависимые панели/страницы вечно показывали спиннер, хотя пользователь уже вошёл.
    effect(() => {
      if (this.auth.mode === 'telegram' && this.auth.telegramBound() === true) {
        this.state.refresh();
        void this.notifications.refresh();
        void this.auth.loadMe();
        void this.tryRedeemPendingInvite();
      }
    }, { allowSignalWrites: true });
  }

  /**
   * Погашение кода инвайта, с которым гость попал на /join/:code до входа/регистрации
   * (JoinInviteComponent → PendingInviteService.set перед уходом на /login или /telegram-bind) —
   * срабатывает на том же переходе "только что аутентифицировался", что и первичная загрузка
   * семей выше. consume() возвращает null почти всегда (обычный вход без ожидающего инвайта) —
   * тогда это no-op.
   */
  private async tryRedeemPendingInvite(): Promise<void> {
    const code = this.pendingInvite.consume();
    if (!code) return;

    try {
      const result = await this.api.redeemInvite(code);
      this.toast.success(
        result.status === 'joined'
          ? 'Вы присоединились к семье.'
          : 'Заявка отправлена, ожидайте подтверждения администратором.',
      );
      await this.state.refresh();
      if (result.familyId) await this.router.navigate(['/families', result.familyId]);
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось погасить приглашение.');
    }
  }

  ngOnInit(): void {
    this.tg.init();
    this.subscribeToRouter();
    void this.initAuth();
  }

  private async initAuth(): Promise<void> {
    // Админ-панель (ADR-0009) — отдельная identity-система, не PWA/Telegram. window.location, не
    // this.router.url: ngOnInit может выполниться до того, как роутер зафиксировал первую
    // навигацию, а этот вызов должен НИКОГДА не уйти в /api/auth/me на /admin/* — именно этот
    // фоновый 401 и уводил с /admin/login на обычный /login (см. auth.interceptor.ts).
    if (window.location.pathname.startsWith('/admin')) return;

    if (this.auth.mode === 'telegram') {
      // Реальный Telegram Mini App: TelegramId может быть ещё не привязан ни к одному аккаунту
      // (TelegramMiniAppAuthenticationHandler — lookup-only, отклонит любой запрос до привязки).
      // Дожидаемся результата — грузить семьи/профиль отсюда напрямую не нужно: telegramBound()
      // пишется внутри ensureTelegramBound() в любом случае (true и false), и эффект в
      // конструкторе сам среагирует на переход в true. Именно поэтому тем же эффектом (а не
      // отдельным вызовом здесь) покрывается и случай более ПОЗДНЕЙ привязки через
      // TelegramBindComponent — тогда initAuth() уже не выполняется повторно (ngOnInit — один
      // раз), и без общего эффекта грузить семьи/профиль было бы некому.
      if (this.tg.isInsideTelegram()) {
        await this.auth.ensureTelegramBound();
        return;
      }

      // Dev-заголовок (X-Dev-TelegramId): DevAuthenticationHandler авто-создаёт пользователя,
      // привязка не нужна — telegramBound() для этого пути остаётся null навсегда, эффект выше
      // никогда не сработает, поэтому грузим сразу же явно.
      void this.auth.loadMe();
      this.state.refresh();
      void this.notifications.refresh();
      return;
    }

    // PWA: без cookie-сессии /api/families ответит 401 — сначала убеждаемся, что вошли, иначе
    // неаутентифицированный пользователь на /login увидел бы лишний баннер ошибки. Сам refresh()
    // при успехе — забота эффекта в конструкторе (сработает и сейчас, если уже была валидная
    // cookie-сессия, и позже — после интерактивного входа/регистрации).
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
        this.currentUrl.set(e.urlAfterRedirects);
        this.moreSheetOpen.set(false);
        this.familySwitcherOpen.set(false);
        // Редизайн v2 — бейдж уведомлений: обновляем счётчик на каждой навигации (дёшево, один
        // COUNT-запрос), пока показан таб-бар/сайдбар — покрывает и "прочитано на другом
        // устройстве", не только markNotificationRead в этой же вкладке.
        if (this.showTabs()) void this.notifications.refresh();
        this.log.log('nav', 'info', `✓ ${e.urlAfterRedirects}`);
      } else if (e instanceof NavigationError) {
        this.log.log('nav', 'error', `✗ ${e.url}: ${String(e.error)}`);
      }
    });
  }
}
