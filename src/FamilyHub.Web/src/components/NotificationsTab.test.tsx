import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import NotificationsTab from './NotificationsTab'
import { NotificationType, type AppNotification } from '../types'

const { getNotifications, markNotificationRead } = vi.hoisted(() => ({
  getNotifications: vi.fn(),
  markNotificationRead: vi.fn(),
}))

vi.mock('../api', () => ({
  ApiError: class ApiError extends Error {
    status: number
    constructor(status: number, message: string) {
      super(message)
      this.status = status
    }
  },
  getNotifications,
  markNotificationRead,
}))

const unread: AppNotification = {
  id: 'n1',
  type: NotificationType.MedicationExpiringSoon,
  title: 'Аспирин',
  body: 'Срок истекает',
  relatedEntityId: 'm1',
  createdAt: '2026-01-01',
  isRead: false,
  readAt: null,
}

describe('NotificationsTab', () => {
  beforeEach(() => {
    getNotifications.mockReset().mockResolvedValue([unread])
    markNotificationRead.mockReset()
  })

  it('loads notifications and toggles the unreadOnly filter', async () => {
    const user = userEvent.setup()
    render(<NotificationsTab />)
    await waitFor(() => expect(getNotifications).toHaveBeenCalledWith(false))

    await user.click(screen.getByRole('checkbox'))

    await waitFor(() => expect(getNotifications).toHaveBeenCalledWith(true))
  })

  it('marks an unread notification as read', async () => {
    const user = userEvent.setup()
    render(<NotificationsTab />)
    await screen.findByText('Аспирин')

    await user.click(screen.getByText('Отметить прочитанным'))

    await waitFor(() => expect(markNotificationRead).toHaveBeenCalledWith('n1'))
  })
})
