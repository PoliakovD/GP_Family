import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import FamiliesTab from './FamiliesTab'
import { FamilyRole, MemberStatus, type FamilySummary } from '../types'

const { createFamily, createInvite, getPendingMembers, redeemInvite, approveMember, rejectMember } = vi.hoisted(() => ({
  createFamily: vi.fn(),
  createInvite: vi.fn(),
  getPendingMembers: vi.fn(),
  redeemInvite: vi.fn(),
  approveMember: vi.fn(),
  rejectMember: vi.fn(),
}))

vi.mock('../api', () => ({
  ApiError: class ApiError extends Error {
    status: number
    constructor(status: number, message: string) {
      super(message)
      this.status = status
    }
  },
  createFamily,
  createInvite,
  getPendingMembers,
  redeemInvite,
  approveMember,
  rejectMember,
}))

function family(overrides: Partial<FamilySummary> = {}): FamilySummary {
  return { id: 'f1', name: 'Тестовая семья', myRole: FamilyRole.Admin, myStatus: MemberStatus.Active, ...overrides }
}

describe('FamiliesTab', () => {
  beforeEach(() => {
    createFamily.mockReset()
    createInvite.mockReset()
    getPendingMembers.mockReset()
    redeemInvite.mockReset()
    approveMember.mockReset()
    rejectMember.mockReset()
  })

  it('shows admin-only actions only for an Active Admin membership', () => {
    render(<FamiliesTab families={[family()]} onChanged={vi.fn()} />)

    expect(screen.getByText('Заявки на вступление')).toBeInTheDocument()
    expect(screen.getByText('Создать инвайт')).toBeInTheDocument()
  })

  it('hides admin-only actions for a plain Member', () => {
    render(<FamiliesTab families={[family({ myRole: FamilyRole.Member })]} onChanged={vi.fn()} />)

    expect(screen.queryByText('Заявки на вступление')).not.toBeInTheDocument()
  })

  it('hides admin-only actions while PendingApproval', () => {
    render(<FamiliesTab families={[family({ myStatus: MemberStatus.PendingApproval })]} onChanged={vi.fn()} />)

    expect(screen.queryByText('Создать инвайт')).not.toBeInTheDocument()
  })

  it('creates a family and notifies the parent', async () => {
    createFamily.mockResolvedValue({ id: 'new-1' })
    const onChanged = vi.fn()
    const user = userEvent.setup()
    render(<FamiliesTab families={[]} onChanged={onChanged} />)

    await user.type(screen.getByPlaceholderText('Название семьи'), 'Моя семья')
    await user.click(screen.getByText('Создать семью'))

    await waitFor(() => expect(createFamily).toHaveBeenCalledWith('Моя семья'))
    expect(onChanged).toHaveBeenCalled()
  })

  it('shows a joined message when redeem resolves to joined', async () => {
    redeemInvite.mockResolvedValue({ status: 'joined' })
    const user = userEvent.setup()
    render(<FamiliesTab families={[]} onChanged={vi.fn()} />)

    await user.type(screen.getByPlaceholderText('Код инвайта'), 'CODE1')
    await user.click(screen.getByText('Присоединиться'))

    await waitFor(() => expect(screen.getByText('Вы присоединились к семье.')).toBeInTheDocument())
  })

  it('shows a pending message when redeem resolves to pending_approval', async () => {
    redeemInvite.mockResolvedValue({ status: 'pending_approval' })
    const user = userEvent.setup()
    render(<FamiliesTab families={[]} onChanged={vi.fn()} />)

    await user.type(screen.getByPlaceholderText('Код инвайта'), 'CODE2')
    await user.click(screen.getByText('Присоединиться'))

    await waitFor(() =>
      expect(screen.getByText('Заявка отправлена, ожидайте подтверждения администратором.')).toBeInTheDocument(),
    )
  })
})
