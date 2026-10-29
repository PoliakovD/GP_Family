import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { SwUpdate, type UnrecoverableStateEvent, type VersionEvent } from '@angular/service-worker';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AppNoticesComponent } from './app-notices.component';

/**
 * Автообновление PWA: плашка «вышла новая версия» больше не требует клика — страница
 * перезагружается сама, но только в безопасный момент и никогда в цикле.
 */
describe('AppNoticesComponent — автообновление PWA', () => {
  const IDLE_MS = 90_000;
  // Свежие Subject'ы на каждый тест: подписки компонента живут до перезагрузки страницы,
  // и без этого события утекали бы в экземпляры из предыдущих тестов.
  let versions: Subject<VersionEvent>;
  let unrecoverable: Subject<UnrecoverableStateEvent>;
  let sw: {
    isEnabled: boolean;
    versionUpdates: Subject<VersionEvent>;
    unrecoverable: Subject<UnrecoverableStateEvent>;
    activateUpdate: ReturnType<typeof vi.fn>;
  };

  let visibility: 'visible' | 'hidden' = 'visible';
  let fixture: ComponentFixture<AppNoticesComponent> | null = null;

  const versionReady = (hash: string) =>
    versions.next({ type: 'VERSION_READY', currentVersion: { hash: 'old' }, latestVersion: { hash } });
  const cacheBroken = () => unrecoverable.next({ type: 'UNRECOVERABLE_STATE', reason: 'test' });

  const create = () => {
    fixture = TestBed.createComponent(AppNoticesComponent);
    const reload = vi.spyOn(fixture.componentInstance, 'reload').mockImplementation(() => undefined);
    return { fixture, component: fixture.componentInstance, reload };
  };

  beforeEach(() => {
    vi.useFakeTimers();
    visibility = 'visible';
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => visibility });
    try { sessionStorage.clear(); } catch { /* приватный режим */ }

    versions = new Subject<VersionEvent>();
    unrecoverable = new Subject<UnrecoverableStateEvent>();
    sw = {
      isEnabled: true,
      versionUpdates: versions,
      unrecoverable,
      activateUpdate: vi.fn(() => Promise.resolve(true)),
    };
    TestBed.configureTestingModule({ providers: [{ provide: SwUpdate, useValue: sw }] });
  });

  afterEach(() => {
    fixture?.destroy();
    fixture = null;
    vi.useRealTimers();
    vi.restoreAllMocks();
    document.body.innerHTML = '';
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' });
  });

  it('применяет новую версию сама после паузы без действий', async () => {
    const { component, reload } = create();

    versionReady('hash-1');
    expect(component.updateReady()).toBe(false);

    await vi.advanceTimersByTimeAsync(IDLE_MS);
    expect(sw.activateUpdate).toHaveBeenCalledTimes(1);
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('откладывает перезагрузку, пока пользователь что-то делает', async () => {
    const { reload } = create();

    versionReady('hash-1');
    await vi.advanceTimersByTimeAsync(IDLE_MS - 10_000);
    window.dispatchEvent(new Event('pointerdown'));
    await vi.advanceTimersByTimeAsync(IDLE_MS - 10_000);
    expect(reload).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(10_000);
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('перезагружается сразу, если вкладка скрыта', async () => {
    visibility = 'hidden';
    const { reload } = create();

    versionReady('hash-1');
    await vi.advanceTimersByTimeAsync(0);
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('не теряет несохранённый ввод: показывает кнопку вместо перезагрузки', async () => {
    const form = document.createElement('form');
    form.className = 'ng-dirty';
    document.body.appendChild(form);
    const { component, reload } = create();

    versionReady('hash-1');
    await vi.advanceTimersByTimeAsync(IDLE_MS);

    expect(reload).not.toHaveBeenCalled();
    expect(component.updateReady()).toBe(true);
  });

  it('не зацикливается: если версия снова «готова» после перезагрузки — плашка', async () => {
    const { component, reload } = create();

    versionReady('hash-1');
    await vi.advanceTimersByTimeAsync(IDLE_MS);
    expect(reload).toHaveBeenCalledTimes(1);

    versionReady('hash-1');
    await vi.advanceTimersByTimeAsync(IDLE_MS);
    expect(reload).toHaveBeenCalledTimes(1);
    expect(component.updateReady()).toBe(true);
  });

  it('сломанный кэш перезагружает сразу, но повтор подряд — уже плашка', async () => {
    const { component, reload } = create();

    cacheBroken();
    expect(reload).toHaveBeenCalledTimes(1);

    cacheBroken();
    expect(reload).toHaveBeenCalledTimes(1);
    expect(component.updateFailed()).toBe(true);

    await vi.advanceTimersByTimeAsync(61_000);
    cacheBroken();
    expect(reload).toHaveBeenCalledTimes(2);
  });
});
