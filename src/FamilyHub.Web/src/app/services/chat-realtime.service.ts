import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import type { HubConnection } from '@microsoft/signalr';
import { ApiService, ApiError } from './api.service';
import { DevLoggerService } from './dev-logger.service';
import type { ChatConfig, ChatMessageChangedEvent } from '../models/types';

/** Ленивая загрузка клиента SignalR — токеном, чтобы тесты подставляли поддельный хаб (vi.mock модулей
 * юнит-тест-билдер Angular не поддерживает: зависимости уже собраны в бандл). */
export const SIGNALR_LOADER = new InjectionToken<() => Promise<typeof import('@microsoft/signalr')>>('SIGNALR_LOADER', {
  providedIn: 'root',
  factory: () => () => import('@microsoft/signalr'),
});

/** Повторы автопереподключения SignalR; после последнего — своя пауза и новый старт с нуля. */
const RECONNECT_DELAYS_MS = [0, 2_000, 10_000, 30_000];
const RESTART_AFTER_CLOSE_MS = 60_000;

/**
 * Единственное на приложение соединение с хабом чата семьи (ADR-0019). Хаб раздаёт только события
 * «сообщение N в семье F изменилось» — содержимое каждый экран перечитывает сам через REST (у карточек
 * ссылок видимость под конкретного зрителя). Здесь же — счётчик непрочитанного для бейджей и сигнал
 * «смотрю чат семьи F», который подавляет уведомления о том, что и так на экране.
 *
 * Вход в хаб — по минутному тикету из POST /api/chat/hub-ticket (берётся заново на каждое подключение):
 * WebSocket не умеет ни заголовок `tma` Mini App, ни надёжно cookie. Сам клиент `@microsoft/signalr`
 * грузится лениво — в начальный бандл не попадает.
 */
@Injectable({ providedIn: 'root' })
export class ChatRealtimeService {
  private readonly api = inject(ApiService);
  private readonly log = inject(DevLoggerService);
  private readonly loadSignalR = inject(SIGNALR_LOADER);

  /** familyId → непрочитанные чужие сообщения. */
  readonly unreadByFamily = signal<Record<string, number>>({});
  readonly totalUnread = computed(() => Object.values(this.unreadByFamily()).reduce((a, b) => a + b, 0));
  readonly connected = signal(false);
  /** Растёт на каждом переподключении — экраны чата перечитывают ленту (события за время обрыва потеряны). */
  readonly resyncTick = signal(0);

  private connection: HubConnection | null = null;
  private starting: Promise<void> | null = null;
  private stopped = true;
  private restartTimer: ReturnType<typeof setTimeout> | null = null;
  private unreadTimer: ReturnType<typeof setTimeout> | null = null;
  private viewingFamilyId: string | null = null;
  private readonly listeners = new Set<(e: ChatMessageChangedEvent) => void>();
  private configPromise: Promise<ChatConfig> | null = null;

  constructor() {
    // Скрытая вкладка/свёрнутый Mini App — уже «не смотрю»: пусть придёт уведомление.
    document.addEventListener('visibilitychange', () => void this.sendViewing());
  }

  /** Идемпотентно: повторные вызовы (смена auth-состояния, открытие чата) не плодят соединений. */
  start(): Promise<void> {
    this.stopped = false;
    void this.refreshUnread();
    if (this.connection) return Promise.resolve();
    this.starting ??= this.connect().finally(() => (this.starting = null));
    return this.starting;
  }

  /** Выход из аккаунта: соединение несёт старую личность, закрываем его. */
  async stop(): Promise<void> {
    this.stopped = true;
    this.clearRestart();
    this.unreadByFamily.set({});
    const connection = this.connection;
    this.connection = null;
    this.connected.set(false);
    await connection?.stop().catch(() => undefined);
  }

  /** Подписка на события хаба; возвращает отписку. */
  onMessageChanged(listener: (e: ChatMessageChangedEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** Экран чата открыт (familyId) или закрыт (null). Переживает переподключение. */
  setViewing(familyId: string | null): void {
    this.viewingFamilyId = familyId;
    void this.sendViewing();
  }

  /** Лимиты и набор реакций — один раз на сессию. */
  getConfig(): Promise<ChatConfig> {
    this.configPromise ??= this.api.getChatConfig().catch((e) => {
      this.configPromise = null;
      throw e;
    });
    return this.configPromise;
  }

  /** Оптимистично обнуляем бейдж семьи сразу после отметки «прочитано». */
  markReadLocal(familyId: string): void {
    this.unreadByFamily.update((m) => ({ ...m, [familyId]: 0 }));
  }

  async refreshUnread(): Promise<void> {
    try {
      const items = await this.api.getChatUnread();
      this.unreadByFamily.set(Object.fromEntries(items.map((i) => [i.familyId, i.count])));
    } catch (e) {
      // Бейдж — вспомогательная индикация (тот же принцип, что NotificationStateService).
      this.log.log('chat', 'error', `getChatUnread failed: ${e instanceof ApiError ? e.message : String(e)}`);
    }
  }

  private async connect(): Promise<void> {
    try {
      const signalR = await this.loadSignalR();
      const connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/chat', {
          accessTokenFactory: async () => (await this.api.getChatHubTicket()).ticket,
        })
        .withAutomaticReconnect(RECONNECT_DELAYS_MS)
        .configureLogging(signalR.LogLevel.Warning)
        .build();

      connection.on('messageChanged', (e: ChatMessageChangedEvent) => this.dispatch(e));
      connection.onreconnecting(() => this.connected.set(false));
      connection.onreconnected(() => {
        this.connected.set(true);
        this.resyncTick.update((n) => n + 1);
        void this.sendViewing();
        void this.refreshUnread();
      });
      connection.onclose(() => {
        this.connected.set(false);
        if (this.connection === connection) this.connection = null;
        this.scheduleRestart();
      });

      this.connection = connection;
      await connection.start();
      if (this.stopped) {
        await connection.stop();
        return;
      }
      this.connected.set(true);
      this.log.log('chat', 'info', 'hub connected');
      void this.sendViewing();
    } catch (e) {
      // Нет согласия (403 на тикет), сеть, прокси без WebSocket и т.п. — чат работает и без realtime
      // (лента грузится по REST), просто без мгновенных обновлений; пробуем снова позже.
      this.log.log('chat', 'error', `hub connect failed: ${e instanceof Error ? e.message : String(e)}`);
      this.connection = null;
      this.scheduleRestart();
    }
  }

  private dispatch(e: ChatMessageChangedEvent): void {
    for (const listener of this.listeners) listener(e);
    // Счётчик — с сервера (там же правила «не своё, не удалённое, после вступления»), с небольшой
    // склейкой: пачка событий даёт один запрос.
    if (this.unreadTimer) clearTimeout(this.unreadTimer);
    this.unreadTimer = setTimeout(() => void this.refreshUnread(), 400);
  }

  private async sendViewing(): Promise<void> {
    const connection = this.connection;
    if (!connection || !this.connected()) return;
    const familyId = document.visibilityState === 'visible' ? this.viewingFamilyId : null;
    try {
      if (familyId) await connection.invoke('Viewing', familyId);
      else await connection.invoke('StopViewing');
    } catch (e) {
      this.log.log('chat', 'error', `viewing signal failed: ${e instanceof Error ? e.message : String(e)}`);
    }
  }

  private scheduleRestart(): void {
    if (this.stopped || this.restartTimer) return;
    this.restartTimer = setTimeout(() => {
      this.restartTimer = null;
      if (!this.stopped && !this.connection) void this.start();
    }, RESTART_AFTER_CLOSE_MS);
  }

  private clearRestart(): void {
    if (this.restartTimer) clearTimeout(this.restartTimer);
    this.restartTimer = null;
  }
}
