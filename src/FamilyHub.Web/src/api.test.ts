import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const { getInitData } = vi.hoisted(() => ({ getInitData: vi.fn(() => '') }))
vi.mock('./telegram', () => ({ getInitData }))

import { createFamily, createInvite, deleteMedication, getNotifications, uploadAttachment } from './api'

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

describe('api', () => {
  beforeEach(() => {
    localStorage.clear()
    getInitData.mockReturnValue('')
    vi.stubGlobal('fetch', vi.fn())
  })

  afterEach(() => {
    window.history.pushState({}, '', '/')
    vi.unstubAllGlobals()
  })

  it('sends an Authorization header built from Telegram initData when inside Telegram', async () => {
    getInitData.mockReturnValue('signed-init-data')
    vi.mocked(fetch).mockResolvedValue(jsonResponse({ id: '1' }, 201))

    await createFamily('Иванова')

    const [, init] = vi.mocked(fetch).mock.calls[0]
    expect((init!.headers as Record<string, string>).Authorization).toBe('tma signed-init-data')
  })

  it('falls back to X-Dev-TelegramId from localStorage outside Telegram', async () => {
    localStorage.setItem('familyhub:devTgId', '4242')
    vi.mocked(fetch).mockResolvedValue(jsonResponse([]))

    await getNotifications(false)

    const [, init] = vi.mocked(fetch).mock.calls[0]
    expect((init!.headers as Record<string, string>)['X-Dev-TelegramId']).toBe('4242')
  })

  it('persists devTgId from the query string into localStorage and builds the right URL', async () => {
    window.history.pushState({}, '', '/?devTgId=777')
    vi.mocked(fetch).mockResolvedValue(jsonResponse([]))

    await getNotifications(true)

    expect(localStorage.getItem('familyhub:devTgId')).toBe('777')
    const [url] = vi.mocked(fetch).mock.calls[0]
    expect(url).toBe('/api/notifications?unreadOnly=true')
  })

  it('createInvite sends the default targetUserId/role/maxUses/expiresAt', async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse({ id: '1', code: 'ABC', maxUses: 1, expiresAt: null }, 201))

    await createInvite('family-1')

    const [url, init] = vi.mocked(fetch).mock.calls[0]
    expect(url).toBe('/api/families/family-1/invites')
    expect(init!.method).toBe('POST')
    expect(JSON.parse(init!.body as string)).toEqual({
      targetUserId: null,
      assignedRole: 0,
      maxUses: 1,
      expiresAt: null,
    })
  })

  it('uploadAttachment sends multipart FormData without a JSON Content-Type header', async () => {
    vi.mocked(fetch).mockResolvedValue(
      jsonResponse({ id: 'a1', fileName: 'x.png', contentType: 'image/png', sizeBytes: 3, uploadedAt: '2026-01-01' }, 201),
    )
    const file = new File(['abc'], 'x.png', { type: 'image/png' })

    await uploadAttachment('record-1', file)

    const [url, init] = vi.mocked(fetch).mock.calls[0]
    expect(url).toBe('/api/medical-records/record-1/attachments')
    expect(init!.body).toBeInstanceOf(FormData)
    expect((init!.headers as Record<string, string>)['Content-Type']).toBeUndefined()
  })

  it('throws an ApiError carrying the response status on non-ok responses', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response('forbidden', { status: 403 }))

    await expect(getNotifications(false)).rejects.toMatchObject({ status: 403 })
  })

  it('resolves to undefined for a 204 No Content response', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(null, { status: 204 }))

    await expect(deleteMedication('med-1')).resolves.toBeUndefined()
  })
})
