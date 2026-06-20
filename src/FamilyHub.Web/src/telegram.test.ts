import { afterEach, describe, expect, it, vi } from 'vitest'
import { getInitData, initTelegram, isInsideTelegram, openExternalLink } from './telegram'

function stubWebApp(overrides: Partial<Window['Telegram']> = {}) {
  window.Telegram = {
    WebApp: {
      initData: '',
      colorScheme: 'light',
      ready: vi.fn(),
      expand: vi.fn(),
      openLink: vi.fn(),
      close: vi.fn(),
      ...overrides.WebApp,
    },
  }
  return window.Telegram.WebApp!
}

describe('telegram', () => {
  afterEach(() => {
    delete window.Telegram
    vi.restoreAllMocks()
  })

  it('getInitData/isInsideTelegram return empty/false without window.Telegram', () => {
    expect(getInitData()).toBe('')
    expect(isInsideTelegram()).toBe(false)
  })

  it('getInitData/isInsideTelegram reflect WebApp.initData when present', () => {
    stubWebApp({ WebApp: { initData: 'abc' } as never })

    expect(getInitData()).toBe('abc')
    expect(isInsideTelegram()).toBe(true)
  })

  it('initTelegram is a no-op without window.Telegram', () => {
    expect(() => initTelegram()).not.toThrow()
  })

  it('initTelegram calls ready/expand on the WebApp when present', () => {
    const webApp = stubWebApp()

    initTelegram()

    expect(webApp.ready).toHaveBeenCalledTimes(1)
    expect(webApp.expand).toHaveBeenCalledTimes(1)
  })

  it('openExternalLink uses WebApp.openLink inside Telegram', () => {
    const webApp = stubWebApp()

    openExternalLink('https://example.com')

    expect(webApp.openLink).toHaveBeenCalledWith('https://example.com')
  })

  it('openExternalLink falls back to window.open outside Telegram', () => {
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null)

    openExternalLink('https://example.com')

    expect(openSpy).toHaveBeenCalledWith('https://example.com', '_blank', 'noopener,noreferrer')
  })
})
