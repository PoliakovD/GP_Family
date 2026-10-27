import { TestBed } from '@angular/core/testing';
import { SwPush } from '@angular/service-worker';
import { Observable, of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from './api.service';
import { DevLoggerService } from './dev-logger.service';
import { PushNotificationService } from './push-notification.service';
import { TelegramService } from './telegram.service';

const OLD_KEY = 'AQID'; // base64url для [1, 2, 3]
const NEW_KEY = 'BAUG'; // base64url для [4, 5, 6]

function fakeSubscription(key: number[] | null, endpoint = 'https://push.example/old') {
  return {
    endpoint,
    options: { applicationServerKey: key ? new Uint8Array(key).buffer : null },
    toJSON: () => ({ endpoint, keys: { p256dh: 'p', auth: 'a' } }),
    unsubscribe: vi.fn().mockResolvedValue(true),
  } as unknown as PushSubscription;
}

describe('PushNotificationService.healAfterKeyRotation', () => {
  let swPush: {
    isEnabled: boolean;
    subscription: Observable<PushSubscription | null>;
    unsubscribe: ReturnType<typeof vi.fn>;
    requestSubscription: ReturnType<typeof vi.fn>;
  };
  let api: { getPushVapidPublicKey: ReturnType<typeof vi.fn>; subscribePush: ReturnType<typeof vi.fn> };
  let permission: NotificationPermission;

  function setup(existing: PushSubscription | null): PushNotificationService {
    swPush = {
      isEnabled: true,
      subscription: of(existing),
      unsubscribe: vi.fn().mockResolvedValue(undefined),
      requestSubscription: vi.fn().mockResolvedValue(fakeSubscription([4, 5, 6], 'https://push.example/new')),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: SwPush, useValue: swPush },
        { provide: ApiService, useValue: api },
        { provide: TelegramService, useValue: { isInsideTelegram: () => false } },
        { provide: DevLoggerService, useValue: { log: vi.fn() } },
      ],
    });
    return TestBed.inject(PushNotificationService);
  }

  beforeEach(() => {
    permission = 'granted';
    api = {
      getPushVapidPublicKey: vi.fn().mockResolvedValue({ publicKey: NEW_KEY }),
      subscribePush: vi.fn().mockResolvedValue(undefined),
    };
    vi.stubGlobal('Notification', { get permission() { return permission; } });
    vi.stubGlobal('PushManager', class {});
    Object.defineProperty(navigator, 'serviceWorker', { value: {}, configurable: true });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    TestBed.resetTestingModule();
  });

  it('resubscribes silently when the subscription was created with an old VAPID key', async () => {
    const service = setup(fakeSubscription([1, 2, 3]));

    await service.healAfterKeyRotation();

    expect(swPush.unsubscribe).toHaveBeenCalledOnce();
    expect(swPush.requestSubscription).toHaveBeenCalledWith({ serverPublicKey: NEW_KEY });
    expect(api.subscribePush).toHaveBeenCalledWith('https://push.example/new', 'p', 'a');
    expect(service.isSubscribed()).toBe(true);
  });

  it('re-registers the same subscription when the key still matches (server may have dropped it)', async () => {
    api.getPushVapidPublicKey.mockResolvedValue({ publicKey: OLD_KEY });
    const service = setup(fakeSubscription([1, 2, 3]));

    await service.healAfterKeyRotation();

    expect(swPush.unsubscribe).not.toHaveBeenCalled();
    expect(swPush.requestSubscription).not.toHaveBeenCalled();
    expect(api.subscribePush).toHaveBeenCalledWith('https://push.example/old', 'p', 'a');
  });

  it('does nothing when the user never enabled push', async () => {
    const service = setup(null);

    await service.healAfterKeyRotation();

    expect(api.subscribePush).not.toHaveBeenCalled();
    expect(swPush.requestSubscription).not.toHaveBeenCalled();
  });

  it('does nothing without granted notification permission', async () => {
    permission = 'default';
    const service = setup(fakeSubscription([1, 2, 3]));

    await service.healAfterKeyRotation();

    expect(api.getPushVapidPublicKey).not.toHaveBeenCalled();
    expect(api.subscribePush).not.toHaveBeenCalled();
  });

  it('runs once per session', async () => {
    const service = setup(fakeSubscription([1, 2, 3]));

    await service.healAfterKeyRotation();
    await service.healAfterKeyRotation();

    expect(swPush.requestSubscription).toHaveBeenCalledOnce();
  });
});
