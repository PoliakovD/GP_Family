import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import MedicationsTab from './MedicationsTab'
import type { Medication } from '../types'

const { getMedications, createMedication, updateMedication, deleteMedication } = vi.hoisted(() => ({
  getMedications: vi.fn(),
  createMedication: vi.fn(),
  updateMedication: vi.fn(),
  deleteMedication: vi.fn(),
}))

vi.mock('../api', () => ({
  ApiError: class ApiError extends Error {
    status: number
    constructor(status: number, message: string) {
      super(message)
      this.status = status
    }
  },
  getMedications,
  createMedication,
  updateMedication,
  deleteMedication,
}))

const sample: Medication = {
  id: 'm1',
  familyId: 'f1',
  name: 'Аспирин',
  instructions: 'По 1 таблетке',
  expiryDate: '2026-12-01',
  quantity: 5,
  createdByUserId: 'u1',
  createdAt: '2026-01-01',
}

describe('MedicationsTab', () => {
  beforeEach(() => {
    getMedications.mockReset().mockResolvedValue([sample])
    createMedication.mockReset()
    updateMedication.mockReset()
    deleteMedication.mockReset()
  })

  it('renders medications loaded for the given family', async () => {
    render(<MedicationsTab familyId="f1" />)

    expect(await screen.findByText('Аспирин')).toBeInTheDocument()
    expect(getMedications).toHaveBeenCalledWith('f1')
  })

  it('submits the form to create a new medication', async () => {
    getMedications.mockResolvedValueOnce([]).mockResolvedValueOnce([sample])
    createMedication.mockResolvedValue(sample)
    const user = userEvent.setup()
    render(<MedicationsTab familyId="f1" />)
    await waitFor(() => expect(getMedications).toHaveBeenCalledTimes(1))

    await user.type(screen.getByPlaceholderText('Название препарата'), 'Аспирин')
    await user.click(screen.getByText('Добавить'))

    await waitFor(() =>
      expect(createMedication).toHaveBeenCalledWith('f1', {
        name: 'Аспирин',
        instructions: null,
        expiryDate: null,
        quantity: 1,
      }),
    )
  })

  it('deletes a medication and refreshes the list', async () => {
    const user = userEvent.setup()
    render(<MedicationsTab familyId="f1" />)
    await screen.findByText('Аспирин')

    await user.click(screen.getByText('Удалить'))

    await waitFor(() => expect(deleteMedication).toHaveBeenCalledWith('m1'))
  })
})
