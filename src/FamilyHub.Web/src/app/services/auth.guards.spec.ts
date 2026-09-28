import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { authGuard, consentGuard, profileGuard } from './auth.guards';
import { AuthService, type ConsentStatus, type Me } from './auth.service';
import { TelegramService } from './telegram.service';

function fakeMe(overrides: Partial<Me> = {}): Me {
  return {
    userId: 'u1', lastName: 'Иванов', firstName: 'Иван', middleName: null, birthDate: '1990-01-01',
    gender: 0, profileComplete: true, provider: 'email', email: 'i@example.com', username: 'ivan',
    tgUsername: null, hasTelegram: false, hasPassword: true, timeZoneId: 'Europe/Moscow',
    ...overrides,
  };
}

describe('auth.guards', () => {
  let auth: {
    mode: 'telegram' | 'pwa';
    me: ReturnType<typeof signal<Me | null>>;
    consent: ReturnType<typeof signal<ConsentStatus | null>>;
    telegramBound: ReturnType<typeof signal<boolean | null>>;
    loadMe: ReturnType<typeof vi.fn>;
    ensureTelegramBound: ReturnType<typeof vi.fn>;
    loadConsentStatus: ReturnType<typeof vi.fn>;
  };
  let tg: { isInsideTelegram: ReturnType<typeof vi.fn> };
  let createUrlTree: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    auth = {
      mode: 'pwa',
      me: signal<Me | null>(null),
      consent: signal<ConsentStatus | null>(null),
      telegramBound: signal<boolean | null>(null),
      loadMe: vi.fn(),
      ensureTelegramBound: vi.fn(),
      loadConsentStatus: vi.fn(),
    };
    tg = { isInsideTelegram: vi.fn().mockReturnValue(false) };
    createUrlTree = vi.fn((commands: unknown[]) => ({ __redirectTo: commands[0] }));

    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: auth },
        { provide: TelegramService, useValue: tg },
        { provide: Router, useValue: { createUrlTree } },
      ],
    });
  });

  function runGuard(guard: typeof authGuard) {
    return TestBed.runInInjectionContext(() => guard({} as never, {} as never));
  }

  describe('authGuard', () => {
    it('PWA mode: passes through without a network call when me() is already cached', async () => {
      auth.me.set(fakeMe());

      await expect(runGuard(authGuard)).resolves.toBe(true);
      expect(auth.loadMe).not.toHaveBeenCalled();
    });

    it('PWA mode: loads me() and passes through on success', async () => {
      auth.loadMe.mockResolvedValue(fakeMe());

      await expect(runGuard(authGuard)).resolves.toBe(true);
    });

    it('PWA mode: redirects to /login when loadMe() resolves null', async () => {
      auth.loadMe.mockResolvedValue(null);

      const result = await runGuard(authGuard);

      expect(result).toEqual({ __redirectTo: '/login' });
    });

    it('Telegram mode: a dev header (not inside Telegram) passes through without checking binding', async () => {
      auth.mode = 'telegram';
      tg.isInsideTelegram.mockReturnValue(false);

      await expect(runGuard(authGuard)).resolves.toBe(true);
      expect(auth.ensureTelegramBound).not.toHaveBeenCalled();
    });

    it('Telegram mode: an already-bound session passes through without a new binding check', async () => {
      auth.mode = 'telegram';
      tg.isInsideTelegram.mockReturnValue(true);
      auth.telegramBound.set(true);

      await expect(runGuard(authGuard)).resolves.toBe(true);
      expect(auth.ensureTelegramBound).not.toHaveBeenCalled();
    });

    it('Telegram mode: redirects to /telegram-bind when the account isn\'t bound', async () => {
      auth.mode = 'telegram';
      tg.isInsideTelegram.mockReturnValue(true);
      auth.ensureTelegramBound.mockResolvedValue(false);

      const result = await runGuard(authGuard);

      expect(result).toEqual({ __redirectTo: '/telegram-bind' });
    });
  });

  describe('consentGuard', () => {
    it('passes through when consent is already cached as accepted', async () => {
      auth.consent.set({ accepted: true, version: '1' });

      await expect(runGuard(consentGuard)).resolves.toBe(true);
      expect(auth.loadConsentStatus).not.toHaveBeenCalled();
    });

    it('redirects to /consent when the loaded status is not accepted', async () => {
      auth.loadConsentStatus.mockResolvedValue({ accepted: false, version: '2' });

      const result = await runGuard(consentGuard);

      expect(result).toEqual({ __redirectTo: '/consent' });
    });

    it('passes through when loadConsentStatus throws — authGuard already handled the redirect', async () => {
      auth.loadConsentStatus.mockRejectedValue(new Error('401'));

      await expect(runGuard(consentGuard)).resolves.toBe(true);
    });
  });

  describe('profileGuard', () => {
    it('passes through when the cached profile is already complete', async () => {
      auth.me.set(fakeMe({ profileComplete: true }));

      await expect(runGuard(profileGuard)).resolves.toBe(true);
      expect(auth.loadMe).not.toHaveBeenCalled();
    });

    it('redirects to /profile-setup on a cached incomplete profile, without an extra loadMe() call', async () => {
      auth.me.set(fakeMe({ profileComplete: false }));

      const result = await runGuard(profileGuard);

      expect(result).toEqual({ __redirectTo: '/profile-setup' });
      expect(auth.loadMe).not.toHaveBeenCalled();
    });

    it('loads me() and passes through once the profile turns out complete', async () => {
      auth.loadMe.mockResolvedValue(fakeMe({ profileComplete: true }));

      await expect(runGuard(profileGuard)).resolves.toBe(true);
    });

    it('redirects to /profile-setup when loadMe() resolves an incomplete profile', async () => {
      auth.loadMe.mockResolvedValue(fakeMe({ profileComplete: false }));

      const result = await runGuard(profileGuard);

      expect(result).toEqual({ __redirectTo: '/profile-setup' });
    });
  });
});
