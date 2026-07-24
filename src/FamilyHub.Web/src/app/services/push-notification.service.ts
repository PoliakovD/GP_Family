import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { SwPush } from '@angular/service-worker';
import { ApiService, ApiError } from './api.service';
import { DevLoggerService } from './dev-logger.service';
import { TelegramService } from './telegram.service';

/** Статический файл, public/push-sw.js — см. его комментарий для контекста. */
const PUSH_SW_URL = '/push-sw.js';

/**
 * Обёртка над Web Push с двумя путями доставки регистрации SW:
 *
 * 1. **ngsw** (`SwPush`, `@angular/service-worker`) — когда Angular ngsw активен (production
 *    `ng build`, вне Telegram, см. `app.config.ts`). ngsw-worker.js сам обрабатывает
 *    push/notificationclick "из коробки" по формату `{"notification":{...}}`.
 * 2. **Ручная регистрация** (`public/push-sw.js`) — когда ngsw недоступен. Ключевой кейс: `ng serve`
 *    (dev-server) НИКОГДА не собирает ngsw-worker.js — это чисто `ng build`-артефакт, никакая
 *    конфигурация `angular.json` этого не меняет. Но именно через `ng serve` (+ ngrok-туннель для
 *    HTTPS) идёт повседневная разработка с dev-панелью и Telegram-вебхуками — там тоже должен
 *    работать push, иначе фичу нельзя протестировать иначе как через отдельный prod-билд.
 *    `push-sw.js` — сознательно without кэширования (нет fetch-обработчика) — не конфликтует
 *    ни с dev-панелью, ни с hot-reload, ни с прокси на API.
 *
 * Оба пути шлют/читают тот же формат подписки и тот же payload — выбор пути прозрачен для UI
 * (SettingsComponent просто спрашивает isSupported/isSubscribed, не зная про ngsw vs ручной SW).
 */
@Injectable({ providedIn: 'root' })
export class PushNotificationService {
  private readonly swPush = inject(SwPush);
  private readonly api = inject(ApiService);
  private readonly log = inject(DevLoggerService);
  private readonly tg = inject(TelegramService);

  readonly isSubscribed = signal(false);

  private vapidPublicKey: string | null = null;
  private manualRegistration: ServiceWorkerRegistration | null = null;

  /**
   * НЕ зависит от того, активен ли ngsw — только от реальной возможности браузера (Push API +
   * Service Worker) и режима (не Telegram). В Telegram push осознанно не предлагаем — там нет
   * смысла (Mini App живёт внутри Telegram, у которого свой канал уведомлений — бот).
   */
  get isSupported(): boolean {
    return !this.tg.isInsideTelegram() && 'serviceWorker' in navigator && 'PushManager' in window;
  }

  private get usesNgsw(): boolean {
    return this.swPush.isEnabled;
  }

  /** Настроен ли Web Push на бэкенде (есть VAPID-ключи). */
  async isBackendConfigured(): Promise<boolean> {
    return (await this.fetchVapidKeyWithRetry()) !== null;
  }

  /**
   * Один retry на транзиентный сбой (бэкенд ещё поднимается/перезапускается) — без него разовая
   * гонка при старте выглядела бы как "push вообще не работает", хотя следующий клик уже сработал бы.
   * Кэширует только УСПЕШНЫЙ результат — если бэкенд ещё не настроен (VAPID не задан, честный 404),
   * повторные вызовы продолжают пробовать заново, а не залипают в "недоступно" навсегда.
   */
  private async fetchVapidKeyWithRetry(): Promise<string | null> {
    if (this.vapidPublicKey) return this.vapidPublicKey;

    for (let attempt = 0; attempt < 2; attempt++) {
      try {
        const { publicKey } = await this.api.getPushVapidPublicKey();
        this.vapidPublicKey = publicKey;
        return publicKey;
      } catch (e) {
        this.log.log('push', attempt === 0 ? 'info' : 'error', `vapid-public-key попытка ${attempt + 1}: ${String(e)}`);
        if (attempt === 0) await new Promise((resolve) => setTimeout(resolve, 600));
      }
    }
    return null;
  }

  /** Синхронизирует isSubscribed с реальным состоянием SW — вызывать при инициализации Настроек. */
  async refreshStatus(): Promise<void> {
    if (!this.isSupported) {
      this.isSubscribed.set(false);
      return;
    }
    try {
      const subscription = await this.currentSubscription();
      this.isSubscribed.set(subscription !== null);
    } catch (e) {
      this.log.log('push', 'error', `refreshStatus: ${String(e)}`);
    }
  }

  async subscribe(): Promise<void> {
    if (!this.isSupported) {
      throw new ApiError(0, 'Push-уведомления недоступны в этом режиме.');
    }

    // Разрешение у браузера спрашиваем ПЕРВЫМ делом, до любых обращений к бэкенду. Раньше проверка
    // VAPID-ключа шла раньше — если бэкенд ещё не настроен (404), клик по тумблеру не показывал
    // пользователю вообще никакого диалога и выглядел "сломанным": ошибка просто улетала в toast,
    // без единого признака, что браузер вообще спросили. requestSubscription()/pushManager.subscribe()
    // ниже сами по себе тоже вызывают этот диалог, но только если разрешение ещё не решено — здесь
    // явно, чтобы отличить "юзер отказал" от "бэкенд не настроен" разными сообщениями.
    const permission = await Notification.requestPermission();
    if (permission !== 'granted') {
      throw new ApiError(0, 'Уведомления не разрешены в браузере.');
    }

    const publicKey = await this.fetchVapidKeyWithRetry();
    if (!publicKey) {
      throw new ApiError(0, 'Push-уведомления сейчас недоступны на сервере — попробуйте позже.');
    }

    const subscription = this.usesNgsw
      ? await this.swPush.requestSubscription({ serverPublicKey: publicKey })
      : await this.subscribeManually(publicKey);

    const json = subscription.toJSON();
    const p256dh = json.keys?.['p256dh'];
    const auth = json.keys?.['auth'];
    if (!json.endpoint || !p256dh || !auth) {
      throw new ApiError(0, 'Браузер вернул неполную push-подписку.');
    }

    await this.api.subscribePush(json.endpoint, p256dh, auth);
    this.isSubscribed.set(true);
  }

  async unsubscribe(): Promise<void> {
    const subscription = await this.currentSubscription();
    if (subscription) {
      await this.api.unsubscribePush(subscription.endpoint);
    }

    if (this.usesNgsw) {
      await this.swPush.unsubscribe();
    } else {
      await subscription?.unsubscribe();
    }
    this.isSubscribed.set(false);
  }

  private async currentSubscription(): Promise<PushSubscription | null> {
    if (this.usesNgsw) {
      return firstValueFrom(this.swPush.subscription);
    }
    const registration = await this.getManualRegistration();
    return registration.pushManager.getSubscription();
  }

  private async subscribeManually(serverPublicKey: string): Promise<PushSubscription> {
    const registration = await this.getManualRegistration();
    return registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: this.urlBase64ToUint8Array(serverPublicKey),
    });
  }

  private async getManualRegistration(): Promise<ServiceWorkerRegistration> {
    if (this.manualRegistration) return this.manualRegistration;
    this.manualRegistration = await navigator.serviceWorker.register(PUSH_SW_URL);
    await navigator.serviceWorker.ready;
    return this.manualRegistration;
  }

  /** VAPID-ключ приходит base64url (см. WebPushOptions на бэкенде) — PushManager.subscribe() ждёт Uint8Array. */
  private urlBase64ToUint8Array(base64: string): Uint8Array {
    const padding = '='.repeat((4 - (base64.length % 4)) % 4);
    const normalized = (base64 + padding).replace(/-/g, '+').replace(/_/g, '/');
    const raw = atob(normalized);
    return Uint8Array.from([...raw].map((c) => c.charCodeAt(0)));
  }
}
