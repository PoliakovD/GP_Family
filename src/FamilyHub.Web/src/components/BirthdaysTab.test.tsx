import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import BirthdaysTab from './BirthdaysTab'
import type { Birthday } from '../types'

const { getBirthdays, createBirthday, updateBirthday, deleteBirthday } = vi.hoisted(() => ({
  getBirthdays: vi.fn(),
  createBirthday: vi.fn(),
  updateBirthday: vi.fn(),
  deleteBirthday: vi.fn(),
}))

vi.mock('../api', () => ({
  ApiError: class ApiError extends Error {
    status: number
    constructor(status: number, message: string) {
      super(message)
      this.status = status
    }
  },
  getBirthdays,
  createBirthday,
  updateBirthday,
  deleteBirthday,
}))

const sample: Birthday = { id: 'b1', familyId: 'f1', personName: 'Бабушка', date: '1950-05-17' }

describe('BirthdaysTab', () => {
  beforeEach(() => {
    getBirthdays.mockReset().mockResolvedValue([sample])
    createBirthday.mockReset()
    updateBirthday.mockReset()
    deleteBirthday.mockReset()
  })

  it('renders birthdays loaded for the given family', async () => {
    render(<BirthdaysTab familyId="f1" />)

    expect(await screen.findByText('Бабушка')).toBeInTheDocument()
    expect(getBirthdays).toHaveBeenCalledWith('f1')
  })

  it('submits the form to create a new birthday', async () => {
    getBirthdays.mockResolvedValueOnce([]).mockResolvedValueOnce([sample])
    createBirthday.mockResolvedValue(sample)
    const user = userEvent.setup()
    render(<BirthdaysTab familyId="f1" />)
    await waitFor(() => expect(getBirthdays).toHaveBeenCalledTimes(1))

    await user.type(screen.getByPlaceholderText('Имя'), 'Дедушка')
    fireEvent.change(document.querySelector('input[type="date"]')!, { target: { value: '1945-01-01' } })
    await user.click(screen.getByText('Добавить'))

    await waitFor(() =>
      expect(createBirthday).toHaveBeenCalledWith('f1', { personName: 'Дедушка', date: '1945-01-01' }),
    )
  })

  it('deletes a birthday and refreshes the list', async () => {
    const user = userEvent.setup()
    render(<BirthdaysTab familyId="f1" />)
    await screen.findByText('Бабушка')

    await user.click(screen.getByText('Удалить'))

    await waitFor(() => expect(deleteBirthday).toHaveBeenCalledWith('b1'))
  })
})
