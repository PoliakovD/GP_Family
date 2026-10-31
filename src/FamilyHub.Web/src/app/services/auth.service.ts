import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { TelegramService } from './telegram.service';
import { DevLoggerService } from './dev-logger.service';

export interface Me {
  userId: string;
  /** ФИО/ДР/пол (identity rework) — все nullable: профиль может быть ещё не заполнен сразу
   * после Telegram-привязки (см. profileComplete/profileGuard). Отчество — единственное
   * реально опциональное поле и после заполнения профиля (есть не у всех). */
  lastName: string | null;
  firstName: string | null;
  middleName: string | null;
  birthDate: string | null;
  /** Gender: 0 = Male, 1 = Female (см. FamilyHub.Domain.Enums.Gender). */
  gender: number | null;
  /** true, когда lastName/firstName/birthDate/gender все заполнены — см. profileGuard. */
  profileComplete: boolean;
  provider: 'telegram' | 'email' | 'dev';
  email: string | null;
  /** Видимый уникальный username (задаётся при PWA-регистрации либо копируется из Telegram при первом входе). */
  username: string | null;
  /** Зеркало Telegram @username — не уникален, обновляется на каждый TG-вход. */
  tgUsername: string | null;
  hasTelegram: boolean;
  hasPassword: boolean;
  /** IANA-часовой пояс, известный серверу (null — ещё не определён); браузер досылает свой при отличии. */
  timeZoneId: string | null;
}

/** Профиль, задаваемый на регистрации/в настройках — см. FamilyHub.Domain.ValueObjects.PersonName. */
export interface ProfileInput {
  lastName: string;
  firstName: string;
  middleName: string | null;
  birthDate: string;
  gender: number;
}

export interface LinkTelegramStart {
  code: string;
  deepLink: string;
  expiresAt: string;
}

export interface UserSessionInfo {
  id: string;
  /** Момент последней ротации refresh-токена (не первый вход — см. TokenService.RefreshAsync). */
  lastSeenAt: string;
  expiresAt: string;
  ipAddress: string | null;
  device: string;
  isCurrent: boolean;
}

export interface ConsentStatus {
  accepted: boolean;
  version: string;
}

export interface ConsentText {
  version: string;
  text: string;
}

/**
 * Итог попытки обновить access-токен по refresh-cookie (см. AuthService.refresh):
 * - `'ok'` — новая пара cookie выдана, исходный запрос можно повторить;
 * - `'unauthorized'` — refresh-сессии больше нет (истекла, отозвана, reuse-detection): ТОЛЬКО
 *   это состояние означает «войдите снова» и даёт право уводить на /login;
 * - `'transient'` — сеть, 429 (rate-limit "auth-session" делится с /me), 5xx, неразобранная
 *   400 CSRF: сессия могла остаться живой, поэтому разлогинивать нельзя — показываем ошибку
 *   запроса, а следующий 401 попробует refresh ещё раз.
 */
export type RefreshOutcome = 'ok' | 'unauthorized' | 'transient';

/**
 * Блокировка Web Locks: refresh-токен одновременно ротирует только одна вкладка. Две вкладки,
 * предъявившие один и тот же токен, для бэкенда неотличимы от кражи — reuse-detection отзывает
 * ВСЕ сессии пользователя (см. TokenService.RefreshAsync), и обе вкладки улетают на /login.
 */
const REFRESH_LOCK = 'familyhub:auth-refresh';

/**
 * Маркер последней успешной ротации cookie (общий на вкладки). Вкладка, дождавшаяся блокировки
 * уже после чужой ротации, видит, что маркер ИЗМЕНИЛСЯ, и понимает, что refresh ей больше не
 * нужен. Именно смена значения, а не время: `Date.now()` в двух вкладках/табах может совпасть до
 * миллисекунды, и сравнение «метка >= момент запроса» ложно решало, что cookie уже обновлена.
 */
const REFRESH_MARKER_KEY = 'familyhub.authRefreshMarker';

function readRefreshMarker(): string {
  try { return localStorage.getItem(REFRESH_MARKER_KEY) ?? ''; } catch { return ''; }
}

function newRefreshMarker(): string {
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

function writeRefreshMarker(): void {
  try { localStorage.setItem(REFRESH_MARKER_KEY, newRefreshMarker()); } catch { /* приватный режим — блокировка вкладок просто не сработает */ }
}

/**
 * Аутентификация в двух окружениях (этап 2 п.2.4): Telegram Mini App (initData через
 * интерцептор) и PWA (email+пароль, cookie-сессия). Плюс статус согласия ПДн (задача 2.3).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly tg = inject(TelegramService);
  private readonly log = inject(DevLoggerService);

  readonly me = signal<Me | null>(null);
  readonly consent = signal<ConsentStatus | null>(null);

  /**
   * Итог последнего loadMe(): 'unauthorized' — настоящий 401 (me точно обнулён), 'transient' —
   * 429/сетевой сбой/5xx (me НЕ обнулён — см. loadMe()), null — последний вызов успешен либо
   * ещё не было ни одного. Гарды (authGuard/profileGuard) читают это сразу после await loadMe(),
   * чтобы не путать «сбой запроса» с «не аутентифицирован»/«профиль не заполнен» — иначе временный
   * сбой на /api/auth/me (тот же rate-limit "auth", под который подпадает сам /me) уводил на
   * /login или /profile-setup аутентифицированного пользователя с полностью заполненным профилем.
   */
  readonly meLoadError = signal<'unauthorized' | 'transient' | null>(null);

  /**
   * null — ещё не проверяли; true/false — известный результат последнего /telegram/init.
   * Только для реального Telegram Mini App (initData) — dev-заголовок это не использует
   * (DevAuthenticationHandler по-прежнему авто-создаёт пользователя, привязка не нужна).
   */
  readonly telegramBound = signal<boolean | null>(null);

  /**
   * Идёт ли прямо сейчас POST /api/auth/refresh. Автообновление PWA (AppNoticesComponent) ждёт
   * завершения, прежде чем перезагружать страницу: reload посреди ротации оставил бы браузер
   * со старой refresh-cookie, и следующий refresh бэкенд счёл бы reuse-detection — с отзывом
   * ВСЕХ сессий пользователя (см. TokenService.RefreshAsync).
   */
  readonly refreshInFlight = signal(false);

  /** Общий in-flight запрос — см. loadMe(). */
  private pendingLoadMe: Promise<Me | null> | null = null;
  private pendingTelegramInit: Promise<boolean> | null = null;
  /** Общий in-flight refresh — параллельные 401 не должны бить по /api/auth/refresh каждый отдельно. */
  private pendingRefresh: Promise<RefreshOutcome> | null = null;

  /** telegram/dev — вход неявный (заголовки интерцептора); pwa — нужна cookie-сессия. */
  get mode(): 'telegram' | 'pwa' {
    return this.tg.isInsideTelegram() || this.tg.getDevTelegramId() ? 'telegram' : 'pwa';
  }

  /**
   * null — не аутентифицирован (в PWA-режиме это сигнал показать /login).
   * authGuard вызывает это на КАЖДОЙ навигации, пока auth.me() пуст, а AppComponent — ещё
   * и при старте приложения; без дедупликации несколько таких вызовов, случившихся почти
   * одновременно (быстрое переключение вкладок сразу после открытия), уходили отдельными
   * HTTP-запросами и упирались в rate-limit "auth" эндпоинта (429). Пока предыдущий запрос
   * не завершился, повторные вызовы просто ждут тот же промис вместо нового запроса.
   */
  loadMe(): Promise<Me | null> {
    if (this.pendingLoadMe) {
      this.log.log('auth', 'info', 'loadMe() — переиспользую уже летящий запрос');
      return this.pendingLoadMe;
    }

    this.log.log('auth', 'info', 'GET /api/auth/me');
    this.pendingLoadMe = (async () => {
      try {
        return await this.fetchMe();
      } catch (e) {
        // 401 — по-настоящему не аутентифицирован, обнуляем. Любая другая ошибка (429 от
        // rate-limit'а, сетевой сбой, 5xx) — транзиентная и НЕ должна затирать уже известное
        // успешное состояние: иначе разовый сбой повторного вызова (например, из ngOnInit
        // компонента настроек) выглядел бы как разлогин и намертво вешал экран на "Загрузка…",
        // хотя пользователь всё ещё полноценно аутентифицирован (особенно заметно в
        // Telegram-режиме, где сессия per-request и в принципе не может "потеряться").
        const status = e instanceof HttpErrorResponse ? e.status : 0;
        // /api/auth/me намеренно лежит в SESSION_LESS_AUTH_PATHS (аноним на публичной странице
        // не должен улетать на /login), поэтому тихое обновление для него делает не интерцептор,
        // а именно этот метод. В Telegram/dev PWA-cookie нет — refresh там заведомо 401.
        if (status === 401 && this.mode === 'pwa') {
          const outcome = await this.refresh();
          if (outcome === 'ok') {
            try {
              return await this.fetchMe();
            } catch (e2) {
              return this.handleLoadMeError(e2);
            }
          }
          if (outcome === 'transient') {
            // Refresh не удался по временной причине — сессия, скорее всего, жива: не обнуляем
            // me() и помечаем сбой как транзиентный (гарды не выгоняют на /login).
            this.meLoadError.set('transient');
            this.log.log('auth', 'error', 'GET /api/auth/me ✗ 401, refresh временно недоступен');
            return this.me();
          }
        }
        return this.handleLoadMeError(e);
      } finally {
        this.pendingLoadMe = null;
      }
    })();

    return this.pendingLoadMe;
  }

  /** Успешный GET /api/auth/me — общая точка для первого вызова и повтора после refresh. */
  private async fetchMe(): Promise<Me> {
    const me = await firstValueFrom(this.http.get<Me>('/api/auth/me'));
    this.me.set(me);
    this.meLoadError.set(null);
    this.log.log('auth', 'info', `GET /api/auth/me ✓ provider=${me.provider}`);
    return me;
  }

  private handleLoadMeError(e: unknown): Me | null {
    const status = e instanceof HttpErrorResponse ? e.status : 0;
    if (status === 401) {
      this.me.set(null);
      this.meLoadError.set('unauthorized');
    } else {
      this.meLoadError.set('transient');
    }
    this.log.log('auth', 'error', `GET /api/auth/me ✗ ${status} ${this.describeError(e)}`);
    return this.me();
  }

  registerStart(email: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/auth/register/start', { email }));
  }

  async registerConfirm(
    email: string, code: string, password: string, username: string, profile: ProfileInput,
  ): Promise<void> {
    await firstValueFrom(
      this.http.post<void>('/api/auth/register/confirm', { email, code, password, username, ...profile }),
    );
    await this.loadMe();
  }

  /**
   * Единственный путь записи ФИО/ДР/пола после создания User (identity rework) — используется
   * и настройками (SettingsProfileComponent), и первичным экраном сбора профиля после
   * Telegram-привязки (ProfileSetupComponent).
   */
  async updateProfile(profile: ProfileInput): Promise<void> {
    await firstValueFrom(this.http.put<void>('/api/account/profile', profile));
    await this.loadMe();
  }

  /** Проверка занятости username на форме регистрации (blur-хук, см. UsernameRules на бэкенде). */
  async checkUsernameAvailable(username: string): Promise<boolean> {
    const response = await firstValueFrom(
      this.http.get<{ available: boolean }>('/api/auth/username-available', { params: { username } }),
    );
    return response.available;
  }

  async login(email: string, password: string): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/auth/login', { email, password }));
    await this.loadMe();
  }

  /** Забыли пароль: код на email (анти-enumeration — тот же ответ вне зависимости от наличия аккаунта). */
  resetPasswordStart(email: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/auth/reset-password/start', { email }));
  }

  async resetPasswordConfirm(email: string, code: string, newPassword: string): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/auth/reset-password/confirm', { email, code, newPassword }));
    await this.loadMe();
  }

  async logout(): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/auth/logout', {}));
    this.me.set(null);
  }

  /** Logout со всех устройств (после смены пароля / подозрения на компрометацию сессии). */
  async logoutAll(): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/auth/logout-all', {}));
    this.me.set(null);
  }

  /**
   * Обновление access-токена по refresh-cookie. Дедуплицировано (`pendingRefresh`): параллельные
   * 401 и `loadMe()` ждут ОДИН запрос — иначе бэкенд увидит предъявление уже ротированного
   * refresh-токена, сочтёт это reuse-detection и отзовёт всю цепочку сессий (TokenService.RefreshAsync).
   *
   * Дополнительно сериализовано МЕЖДУ вкладками (Web Locks): у каждой вкладки свой `pendingRefresh`,
   * так что две вкладки, перезагрузившиеся после деплоя одновременно, уходили двумя параллельными
   * POST'ами с одной cookie — для бэкенда это неотличимо от кражи. Вкладка, дождавшаяся блокировки
   * уже после чужой ротации, видит изменённый маркер в localStorage и просто повторяет свой запрос
   * с новой cookie, не ротируя токен второй раз.
   *
   * Ошибка не превращается в boolean: `'unauthorized'` (только 401) — сессии действительно нет,
   * `'transient'` (сеть/429/5xx/400 CSRF) — сессия могла быть живой, разлогинивать нельзя.
   * Подробнее — RefreshOutcome.
   */
  refresh(): Promise<RefreshOutcome> {
    if (this.pendingRefresh) return this.pendingRefresh;

    // Маркер снимаем ДО ожидания блокировки: если за это время cookie обновила другая вкладка,
    // он изменится, и наш POST уже не нужен.
    const markerAtStart = readRefreshMarker();
    this.pendingRefresh = this.withRefreshLock(() => this.postRefresh(markerAtStart))
      .finally(() => { this.pendingRefresh = null; });
    return this.pendingRefresh;
  }

  /** Ротация под Web Locks; в браузерах без поддержки (и в тестах) — как раньше, без блокировки. */
  private async withRefreshLock(action: () => Promise<RefreshOutcome>): Promise<RefreshOutcome> {
    const locks = typeof navigator === 'undefined' ? undefined : navigator.locks;
    if (typeof locks?.request !== 'function') return action();

    try {
      return await locks.request(REFRESH_LOCK, action);
    } catch (e) {
      // Блокировка — оптимизация, а не условие работоспособности: без неё просто возможна гонка.
      this.log.log('auth', 'error', `Web Locks: ${this.describeError(e)} — refresh без блокировки`);
      return action();
    }
  }

  private async postRefresh(markerAtStart: string): Promise<RefreshOutcome> {
    if (readRefreshMarker() !== markerAtStart) {
      // Пока ждали блокировку, cookie уже обновила другая вкладка. Второй POST с той же (уже
      // ротированной) cookie бэкенд счёл бы reuse-detection и отозвал все сессии.
      this.log.log('auth', 'info', 'POST /api/auth/refresh — пропуск: cookie уже обновлена другой вкладкой');
      return 'ok';
    }

    this.refreshInFlight.set(true);
    this.log.log('auth', 'info', 'POST /api/auth/refresh');
    try {
      await firstValueFrom(this.http.post<void>('/api/auth/refresh', {}));
      writeRefreshMarker();
      this.log.log('auth', 'info', 'POST /api/auth/refresh ✓');
      return 'ok';
    } catch (e) {
      const status = e instanceof HttpErrorResponse ? e.status : 0;
      this.log.log('auth', 'error', `POST /api/auth/refresh ✗ ${status} ${this.describeError(e)}`);
      return status === 401 ? 'unauthorized' : 'transient';
    } finally {
      this.refreshInFlight.set(false);
    }
  }

  /**
   * Проверяет, привязан ли текущий TelegramId к аккаунту — POST /api/auth/telegram/init.
   * У Telegram нет сессии/токенов (см. TokenService — только PWA): при bound=true дальнейшие
   * запросы просто проходят обычный per-request initData-хендлер, никаких токенов сохранять
   * не нужно. Общий in-flight запрос — authGuard может дёрнуть это на каждой навигации.
   */
  ensureTelegramBound(): Promise<boolean> {
    if (this.pendingTelegramInit) return this.pendingTelegramInit;

    this.pendingTelegramInit = (async () => {
      try {
        const res = await firstValueFrom(
          this.http.post<{ bound: boolean }>('/api/auth/telegram/init', { initData: this.tg.getInitData() }),
        );
        this.telegramBound.set(res.bound);
        return res.bound;
      } catch {
        this.telegramBound.set(false);
        return false;
      } finally {
        this.pendingTelegramInit = null;
      }
    })();

    return this.pendingTelegramInit;
  }

  telegramSendCode(email: string): Promise<void> {
    return firstValueFrom(
      this.http.post<void>('/api/auth/telegram/send-code', { email, initData: this.tg.getInitData() }),
    );
  }

  /** Возвращает profileRequired — true, если после привязки экрану нужно собрать ФИО/ДР/пол. */
  async telegramBind(email: string, code: string): Promise<boolean> {
    const result = await firstValueFrom(
      this.http.post<{ profileRequired: boolean }>(
        '/api/auth/telegram/bind', { email, code, initData: this.tg.getInitData() },
      ),
    );
    this.telegramBound.set(true);
    return result.profileRequired;
  }

  /**
   * Отвязка Telegram (компрометация устройства/бота) — из PWA-сессии, требует пароль для входа
   * (иначе аккаунт остался бы вообще без способа войти). Кнопка в UI скрыта без пароля, но
   * бэкенд тоже это проверяет (409 { code: "password_required" }) — на случай прямого вызова
   * эндпоинта. У Telegram нет сессии/токена: следующий же запрос из этого Telegram-аккаунта
   * получит 401 и уйдёт на повторную привязку (см. authInterceptor).
   */
  async revokeTelegram(): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/auth/telegram/revoke', {}));
    this.telegramBound.set(false);
    await this.loadMe();
  }

  /**
   * Смена пароля из настроек (в отличие от reset-password/*, требует знания текущего пароля —
   * не анонимная). Бэкенд отзывает все прочие сессии и переиздаёт cookie текущей, поэтому здесь
   * НЕ нужно самостоятельно дёргать logout/refresh — сессия остаётся валидной как есть.
   */
  changePassword(currentPassword: string, newPassword: string): Promise<void> {
    return firstValueFrom(
      this.http.post<void>('/api/auth/change-password', { currentPassword, newPassword }),
    );
  }

  /** Список активных сессий (вкладка «Безопасность» → «Мои устройства»). Только PWA — у Telegram сессий нет. */
  getSessions(): Promise<UserSessionInfo[]> {
    return firstValueFrom(this.http.get<UserSessionInfo[]>('/api/auth/sessions'));
  }

  /** Отзыв одной сессии (не текущей — для этого logout()/logoutAll()). */
  revokeSession(id: string): Promise<void> {
    return firstValueFrom(this.http.post<void>(`/api/auth/sessions/${id}/revoke`, {}));
  }

  linkEmailStart(email: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/auth/link-email/start', { email }));
  }

  async linkEmailConfirm(email: string, code: string, password: string): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/auth/link-email/confirm', { email, code, password }));
    await this.loadMe();
  }

  /** Привязка Telegram к текущему (email/PWA) аккаунту — код + deep-link, подтверждение в боте. */
  linkTelegramStart(): Promise<LinkTelegramStart> {
    return firstValueFrom(this.http.post<LinkTelegramStart>('/api/auth/link-telegram/start', {}));
  }

  async loadConsentStatus(): Promise<ConsentStatus> {
    const status = await firstValueFrom(this.http.get<ConsentStatus>('/api/consents/status'));
    this.consent.set(status);
    return status;
  }

  getConsentText(): Promise<ConsentText> {
    return firstValueFrom(this.http.get<ConsentText>('/api/consents/current'));
  }

  async acceptConsent(version: string): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/consents/accept', { version }));
    this.consent.set({ accepted: true, version });
  }

  async deleteAccount(): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/account/delete', { confirm: 'DELETE' }));
    this.me.set(null);
  }

  /**
   * Экспорт данных субъекта (zip). НЕ ссылка/window.open — обычная навигация браузера не
   * идёт через authInterceptor и не получает Authorization-заголовок Telegram-режима (в PWA
   * это работало только случайно, за счёт того, что cookie браузер прикладывает сам). Через
   * HttpClient запрос идёт по общему пайплайну и аутентифицируется одинаково в обоих режимах.
   */
  async exportAccountData(): Promise<Blob> {
    return firstValueFrom(this.http.get('/api/account/export', { responseType: 'blob' }));
  }

  /**
   * Диагностика для DevLogger: "200, но ошибка" почти всегда значит, что тело ответа не
   * распарсилось как JSON (см. loadMe()) — здесь важно увидеть Content-Type и хотя бы кусок
   * тела (HTML interstitial выглядит иначе, чем пустой ответ или обрыв соединения).
   */
  private describeError(e: unknown): string {
    if (!(e instanceof HttpErrorResponse)) return String(e);

    const contentType = e.headers?.get('content-type') ?? 'n/a';
    let bodySnippet: string;
    if (typeof e.error === 'string') {
      bodySnippet = e.error.slice(0, 150);
    } else if (e.error instanceof ProgressEvent) {
      bodySnippet = `ProgressEvent(type=${e.error.type})`; // сетевой обрыв/CORS — тело недоступно
    } else {
      try {
        bodySnippet = JSON.stringify(e.error)?.slice(0, 150) ?? String(e.error);
      } catch {
        bodySnippet = String(e.error);
      }
    }
    return `contentType=${contentType} url=${e.url} body="${bodySnippet}"`;
  }
}
