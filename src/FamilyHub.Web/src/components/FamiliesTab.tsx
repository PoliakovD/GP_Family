import { useState, type FormEvent } from 'react'
import {
  ApiError,
  approveMember,
  createFamily,
  createInvite,
  getPendingMembers,
  redeemInvite,
  rejectMember,
} from '../api'
import { FamilyRole, MemberStatus, type FamilySummary, type PendingMember } from '../types'

interface Props {
  families: FamilySummary[]
  onChanged: () => void
}

function statusLabel(status: number): string {
  return status === MemberStatus.Active ? 'активен' : 'ожидает подтверждения'
}

function roleLabel(role: number): string {
  return role === FamilyRole.Admin ? 'админ' : 'участник'
}

export default function FamiliesTab({ families, onChanged }: Props) {
  const [newFamilyName, setNewFamilyName] = useState('')
  const [inviteCode, setInviteCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<string | null>(null)
  const [pendingByFamily, setPendingByFamily] = useState<Record<string, PendingMember[]>>({})
  const [createdInvite, setCreatedInvite] = useState<{ familyId: string; code: string } | null>(
    null,
  )

  async function handleCreateFamily(e: FormEvent) {
    e.preventDefault()
    if (!newFamilyName.trim()) return
    setBusy(true)
    try {
      await createFamily(newFamilyName.trim())
      setNewFamilyName('')
      setMessage('Семья создана.')
      onChanged()
    } catch (err) {
      setMessage(err instanceof ApiError ? err.message : 'Не удалось создать семью.')
    } finally {
      setBusy(false)
    }
  }

  async function handleRedeem(e: FormEvent) {
    e.preventDefault()
    if (!inviteCode.trim()) return
    setBusy(true)
    try {
      const result = await redeemInvite(inviteCode.trim())
      setMessage(
        result.status === 'joined'
          ? 'Вы присоединились к семье.'
          : 'Заявка отправлена, ожидайте подтверждения администратором.',
      )
      setInviteCode('')
      onChanged()
    } catch (err) {
      setMessage(err instanceof ApiError ? err.message : 'Не удалось погасить инвайт.')
    } finally {
      setBusy(false)
    }
  }

  async function loadPending(familyId: string) {
    try {
      const pending = await getPendingMembers(familyId)
      setPendingByFamily((prev) => ({ ...prev, [familyId]: pending }))
    } catch (err) {
      setMessage(err instanceof ApiError ? err.message : 'Не удалось загрузить заявки.')
    }
  }

  async function handleApprove(familyId: string, userId: string) {
    await approveMember(familyId, userId)
    await loadPending(familyId)
    onChanged()
  }

  async function handleReject(familyId: string, userId: string) {
    await rejectMember(familyId, userId)
    await loadPending(familyId)
    onChanged()
  }

  async function handleCreateInvite(familyId: string) {
    try {
      const invite = await createInvite(familyId)
      setCreatedInvite({ familyId, code: invite.code })
    } catch (err) {
      setMessage(err instanceof ApiError ? err.message : 'Не удалось создать инвайт.')
    }
  }

  return (
    <div>
      {message && <div className="error-banner">{message}</div>}

      <form className="stack" onSubmit={handleCreateFamily}>
        <label>Новая семья</label>
        <input
          value={newFamilyName}
          onChange={(e) => setNewFamilyName(e.target.value)}
          placeholder="Название семьи"
        />
        <button className="primary" type="submit" disabled={busy}>
          Создать семью
        </button>
      </form>

      <form className="stack" onSubmit={handleRedeem}>
        <label>Есть код инвайта?</label>
        <input
          value={inviteCode}
          onChange={(e) => setInviteCode(e.target.value)}
          placeholder="Код инвайта"
        />
        <button className="primary" type="submit" disabled={busy}>
          Присоединиться
        </button>
      </form>

      {families.map((family) => {
        const isAdmin = family.myRole === FamilyRole.Admin
        const pending = pendingByFamily[family.id]
        return (
          <div className="card" key={family.id}>
            <div className="row">
              <strong>{family.name}</strong>
              <span>
                <span className="badge">{roleLabel(family.myRole)}</span>
                <span className="badge">{statusLabel(family.myStatus)}</span>
              </span>
            </div>

            {isAdmin && family.myStatus === MemberStatus.Active && (
              <div style={{ marginTop: 8 }}>
                <div className="row">
                  <button className="secondary" onClick={() => loadPending(family.id)}>
                    Заявки на вступление
                  </button>
                  <button className="secondary" onClick={() => handleCreateInvite(family.id)}>
                    Создать инвайт
                  </button>
                </div>

                {createdInvite?.familyId === family.id && (
                  <p className="muted">
                    Код инвайта: <code>{createdInvite.code}</code> — отправьте его боту командой{' '}
                    <code>/start {createdInvite.code}</code>.
                  </p>
                )}

                {pending && pending.length === 0 && <p className="muted">Заявок нет.</p>}
                {pending?.map((p) => (
                  <div className="row" key={p.userId} style={{ marginTop: 6 }}>
                    <span className="muted">{p.userId}</span>
                    <span>
                      <button className="secondary" onClick={() => handleApprove(family.id, p.userId)}>
                        Принять
                      </button>{' '}
                      <button className="secondary danger" onClick={() => handleReject(family.id, p.userId)}>
                        Отклонить
                      </button>
                    </span>
                  </div>
                ))}
              </div>
            )}
          </div>
        )
      })}

      {families.length === 0 && <p className="muted">Вы пока не состоите ни в одной семье.</p>}
    </div>
  )
}
