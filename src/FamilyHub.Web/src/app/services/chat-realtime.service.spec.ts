import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from './api.service';
import { DevLoggerService } from './dev-logger.service';
import { ChatRealtimeService, SIGNALR_LOADER } from './chat-realtime.service';
import { ChatChange, type ChatMessageChangedEvent } from '../models/types';

/** Поддельное соединение SignalR: запоминает обработчики и вызовы invoke. */
const hub = {
  builds: 0,
  handlers: new Map<string, (arg: unknown) => void>(),
  invoke: vi.fn(),
  stop: vi.fn(),
  tokenFactory: null as null | (() => Promise<string>),
};

function fakeSignalR() {
  class HubConnectionBuilder {
    withUrl(_url: string, options: { accessTokenFactory: () => Promise<string> }) {
      hub.tokenFactory = options.accessTokenFactory;
      return this;
    }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() {
      hub.builds++;
      return {
        on: (name: string, handler: (arg: unknown) => void) => hub.handlers.set(name, handler),
        onreconnecting: vi.fn(),
        onreconnected: vi.fn(),
        onclose: vi.fn(),
        start: vi.fn().mockResolvedValue(undefined),
        stop: hub.stop,
        invoke: hub.invoke,
      };
    }
  }
  return { HubConnectionBuilder, LogLevel: { Warning: 3 } } as unknown as typeof import('@microsoft/signalr');
}

describe('ChatRealtimeService', () => {
  let api: {
    getChatUnread: ReturnType<typeof vi.fn>;
    getChatHubTicket: ReturnType<typeof vi.fn>;
    getChatConfig: ReturnType<typeof vi.fn>;
  };

  function setup(): ChatRealtimeService {
    TestBed.configureTestingModule({
      providers: [
        { provide: ApiService, useValue: api },
        { provide: DevLoggerService, useValue: { log: vi.fn() } },
        { provide: SIGNALR_LOADER, useValue: () => Promise.resolve(fakeSignalR()) },
      ],
    });
    return TestBed.inject(ChatRealtimeService);
  }

  beforeEach(() => {
    hub.builds = 0;
    hub.handlers.clear();
    hub.invoke = vi.fn().mockResolvedValue(undefined);
    hub.stop = vi.fn().mockResolvedValue(undefined);
    api = {
      getChatUnread: vi.fn().mockResolvedValue([{ familyId: 'f1', count: 2 }, { familyId: 'f2', count: 3 }]),
      getChatHubTicket: vi.fn().mockResolvedValue({ ticket: 't-1', expiresAt: '' }),
      getChatConfig: vi.fn().mockResolvedValue({ reactions: ['👍'] }),
    };
  });

  afterEach(() => TestBed.resetTestingModule());

  it('считает непрочитанное по семьям и обнуляет семью локально', async () => {
    const sut = setup();
    await sut.refreshUnread();

    expect(sut.totalUnread()).toBe(5);
    sut.markReadLocal('f1');
    expect(sut.unreadByFamily()).toEqual({ f1: 0, f2: 3 });
    expect(sut.totalUnread()).toBe(3);
  });

  it('одно соединение на повторные start() и тикет на подключение', async () => {
    const sut = setup();
    await Promise.all([sut.start(), sut.start()]);
    await sut.start();

    expect(hub.builds).toBe(1);
    expect(sut.connected()).toBe(true);
    await expect(hub.tokenFactory!()).resolves.toBe('t-1');
  });

  it('раздаёт события подписчикам и отписывает', async () => {
    const sut = setup();
    await sut.start();
    const received: ChatMessageChangedEvent[] = [];
    const unsubscribe = sut.onMessageChanged((e) => received.push(e));

    const event = { familyId: 'f1', messageId: 'm1', change: ChatChange.Created };
    hub.handlers.get('messageChanged')!(event);
    unsubscribe();
    hub.handlers.get('messageChanged')!({ ...event, messageId: 'm2' });

    expect(received).toEqual([event]);
  });

  it('сообщает хабу, какой чат открыт, и снимает отметку', async () => {
    const sut = setup();
    await sut.start();

    sut.setViewing('f1');
    sut.setViewing(null);
    await Promise.resolve();

    expect(hub.invoke).toHaveBeenCalledWith('Viewing', 'f1');
    expect(hub.invoke).toHaveBeenCalledWith('StopViewing');
  });

  it('stop() закрывает соединение и сбрасывает бейджи', async () => {
    const sut = setup();
    await sut.start();
    await sut.refreshUnread();

    await sut.stop();

    expect(hub.stop).toHaveBeenCalled();
    expect(sut.connected()).toBe(false);
    expect(sut.totalUnread()).toBe(0);
  });
});
