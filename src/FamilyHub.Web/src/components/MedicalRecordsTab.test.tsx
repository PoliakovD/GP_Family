import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import MedicalRecordsTab from './MedicalRecordsTab'
import { FamilyRole, MemberStatus, type Attachment, type FamilySummary, type MedicalRecord } from '../types'

const {
  getMedicalRecords,
  createMedicalRecord,
  uploadAttachment,
  getAttachmentUrl,
  shareMedicalRecord,
  unshareMedicalRecord,
  hideMedicalRecord,
  unhideMedicalRecord,
} = vi.hoisted(() => ({
  getMedicalRecords: vi.fn(),
  createMedicalRecord: vi.fn(),
  uploadAttachment: vi.fn(),
  getAttachmentUrl: vi.fn(),
  shareMedicalRecord: vi.fn(),
  unshareMedicalRecord: vi.fn(),
  hideMedicalRecord: vi.fn(),
  unhideMedicalRecord: vi.fn(),
}))

vi.mock('../api', () => ({
  ApiError: class ApiError extends Error {
    status: number
    constructor(status: number, message: string) {
      super(message)
      this.status = status
    }
  },
  getMedicalRecords,
  createMedicalRecord,
  uploadAttachment,
  getAttachmentUrl,
  shareMedicalRecord,
  unshareMedicalRecord,
  hideMedicalRecord,
  unhideMedicalRecord,
}))

const { openExternalLink } = vi.hoisted(() => ({ openExternalLink: vi.fn() }))
vi.mock('../telegram', () => ({ openExternalLink }))

const record: MedicalRecord = {
  id: 'r1',
  ownerUserId: 'u1',
  personName: 'Иван',
  recordDate: '2026-01-01',
  doctor: 'Доктор',
  description: 'Описание',
  createdAt: '2026-01-01',
}
const activeFamily: FamilySummary = { id: 'fam1', name: 'Семья', myRole: FamilyRole.Member, myStatus: MemberStatus.Active }
const attachment: Attachment = { id: 'a1', fileName: 'scan.png', contentType: 'image/png', sizeBytes: 10, uploadedAt: '2026-01-01' }

describe('MedicalRecordsTab', () => {
  beforeEach(() => {
    getMedicalRecords.mockReset().mockResolvedValue([record])
    createMedicalRecord.mockReset()
    uploadAttachment.mockReset()
    getAttachmentUrl.mockReset()
    shareMedicalRecord.mockReset()
    unshareMedicalRecord.mockReset()
    hideMedicalRecord.mockReset()
    unhideMedicalRecord.mockReset()
    openExternalLink.mockReset()
  })

  it('creates a record from the form', async () => {
    getMedicalRecords.mockResolvedValueOnce([]).mockResolvedValueOnce([record])
    createMedicalRecord.mockResolvedValue(record)
    const user = userEvent.setup()
    render(<MedicalRecordsTab activeFamilies={[]} />)
    await waitFor(() => expect(getMedicalRecords).toHaveBeenCalledTimes(1))

    await user.type(screen.getByPlaceholderText('Пациент'), 'Иван')
    fireEvent.change(document.querySelector('input[type="date"]')!, { target: { value: '2026-01-01' } })
    await user.click(screen.getByText('Добавить запись'))

    await waitFor(() =>
      expect(createMedicalRecord).toHaveBeenCalledWith({
        personName: 'Иван',
        recordDate: '2026-01-01',
        doctor: null,
        description: null,
        hideFromFamilyIds: null,
      }),
    )
  })

  it('uploads a file and opens it via the presigned URL', async () => {
    uploadAttachment.mockResolvedValue(attachment)
    getAttachmentUrl.mockResolvedValue({ url: 'https://files.example/scan.png?sig=abc' })
    const user = userEvent.setup()
    render(<MedicalRecordsTab activeFamilies={[]} />)
    await screen.findByText('Иван')
    const file = new File(['scan-bytes'], 'scan.png', { type: 'image/png' })

    await user.upload(document.querySelector('input[type="file"]')!, file)

    await waitFor(() => expect(uploadAttachment).toHaveBeenCalledWith('r1', file))
    const openButton = await screen.findByText('scan.png')

    await user.click(openButton)

    await waitFor(() => expect(getAttachmentUrl).toHaveBeenCalledWith('a1'))
    expect(openExternalLink).toHaveBeenCalledWith('https://files.example/scan.png?sig=abc')
  })

  it('shares, unshares, hides and unhides a record with the selected family', async () => {
    const user = userEvent.setup()
    render(<MedicalRecordsTab activeFamilies={[activeFamily]} />)
    await screen.findByText('Иван')

    await user.selectOptions(screen.getByRole('combobox'), 'fam1')

    await user.click(screen.getByText('Поделиться'))
    await waitFor(() => expect(shareMedicalRecord).toHaveBeenCalledWith('fam1'))

    await user.click(screen.getByText('Перестать делиться'))
    await waitFor(() => expect(unshareMedicalRecord).toHaveBeenCalledWith('fam1'))

    await user.click(screen.getByText('Скрыть от семьи'))
    await waitFor(() => expect(hideMedicalRecord).toHaveBeenCalledWith('r1', ['fam1']))

    await user.click(screen.getByText('Показать семье'))
    await waitFor(() => expect(unhideMedicalRecord).toHaveBeenCalledWith('r1', ['fam1']))
  })
})
