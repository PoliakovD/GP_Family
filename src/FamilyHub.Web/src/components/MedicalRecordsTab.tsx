import { useEffect, useState, type FormEvent } from 'react'
import {
  ApiError,
  createMedicalRecord,
  getAttachmentUrl,
  getMedicalRecords,
  hideMedicalRecord,
  shareMedicalRecord,
  uploadAttachment,
  unhideMedicalRecord,
  unshareMedicalRecord,
} from '../api'
import type { Attachment, FamilySummary, MedicalRecord } from '../types'
import { openExternalLink } from '../telegram'

interface Props {
  activeFamilies: FamilySummary[]
}

const emptyForm = { personName: '', recordDate: '', doctor: '', description: '' }

export default function MedicalRecordsTab({ activeFamilies }: Props) {
  const [items, setItems] = useState<MedicalRecord[]>([])
  const [form, setForm] = useState(emptyForm)
  const [error, setError] = useState<string | null>(null)
  const [shareFamilyByRecord, setShareFamilyByRecord] = useState<Record<string, string>>({})
  // Бэкенд не отдаёт список вложений записи отдельным эндпоинтом — храним то, что
  // загрузили в текущей сессии (ответ POST .../attachments содержит Attachment целиком).
  const [attachmentsByRecord, setAttachmentsByRecord] = useState<Record<string, Attachment[]>>({})

  async function refresh() {
    try {
      setItems(await getMedicalRecords())
      setError(null)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не удалось загрузить анализы.')
    }
  }

  useEffect(() => {
    refresh()
  }, [])

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    if (!form.personName.trim() || !form.recordDate) return
    try {
      await createMedicalRecord({
        personName: form.personName.trim(),
        recordDate: form.recordDate,
        doctor: form.doctor.trim() || null,
        description: form.description.trim() || null,
        hideFromFamilyIds: null,
      })
      setForm(emptyForm)
      await refresh()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не удалось сохранить запись.')
    }
  }

  async function handleUpload(recordId: string, file: File | null) {
    if (!file) return
    try {
      const attachment = await uploadAttachment(recordId, file)
      setAttachmentsByRecord((prev) => ({
        ...prev,
        [recordId]: [...(prev[recordId] ?? []), attachment],
      }))
      setError(null)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не удалось загрузить файл.')
    }
  }

  async function handleOpenAttachment(attachmentId: string) {
    try {
      const { url } = await getAttachmentUrl(attachmentId)
      openExternalLink(url)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не удалось получить ссылку на файл.')
    }
  }

  async function handleShare(recordId: string, share: boolean) {
    const familyId = shareFamilyByRecord[recordId]
    if (!familyId) return
    try {
      if (share) {
        await shareMedicalRecord(familyId)
      } else {
        await unshareMedicalRecord(familyId)
      }
      setError(null)
    } catch (err) {
      setError(
        err instanceof ApiError
          ? err.message
          : 'Действие доступно только владельцу записи.',
      )
    }
  }

  async function handleHide(recordId: string, hide: boolean) {
    const familyId = shareFamilyByRecord[recordId]
    if (!familyId) return
    try {
      if (hide) {
        await hideMedicalRecord(recordId, [familyId])
      } else {
        await unhideMedicalRecord(recordId, [familyId])
      }
      setError(null)
    } catch (err) {
      setError(
        err instanceof ApiError
          ? err.message
          : 'Действие доступно только владельцу записи.',
      )
    }
  }

  return (
    <div>
      {error && <div className="error-banner">{error}</div>}

      <form className="stack" onSubmit={handleSubmit}>
        <input
          placeholder="Пациент"
          value={form.personName}
          onChange={(e) => setForm({ ...form, personName: e.target.value })}
        />
        <input
          type="date"
          value={form.recordDate}
          onChange={(e) => setForm({ ...form, recordDate: e.target.value })}
        />
        <input
          placeholder="Врач (необязательно)"
          value={form.doctor}
          onChange={(e) => setForm({ ...form, doctor: e.target.value })}
        />
        <textarea
          placeholder="Описание (необязательно)"
          value={form.description}
          onChange={(e) => setForm({ ...form, description: e.target.value })}
        />
        <button className="primary" type="submit">
          Добавить запись
        </button>
      </form>

      {items.map((item) => (
        <div className="card" key={item.id}>
          <div className="row">
            <strong>{item.personName}</strong>
            <span className="muted">{item.recordDate}</span>
          </div>
          {item.doctor && <p className="muted">Врач: {item.doctor}</p>}
          {item.description && <p>{item.description}</p>}

          <div style={{ marginTop: 8 }}>
            <input type="file" onChange={(e) => handleUpload(item.id, e.target.files?.[0] ?? null)} />
          </div>

          {activeFamilies.length > 0 && (
            <div style={{ marginTop: 8 }}>
              <select
                value={shareFamilyByRecord[item.id] ?? ''}
                onChange={(e) =>
                  setShareFamilyByRecord((prev) => ({ ...prev, [item.id]: e.target.value }))
                }
              >
                <option value="">Выберите семью…</option>
                {activeFamilies.map((f) => (
                  <option key={f.id} value={f.id}>
                    {f.name}
                  </option>
                ))}
              </select>
              <div className="row" style={{ marginTop: 6 }}>
                <button className="secondary" onClick={() => handleShare(item.id, true)}>
                  Поделиться
                </button>
                <button className="secondary" onClick={() => handleShare(item.id, false)}>
                  Перестать делиться
                </button>
              </div>
              <div className="row" style={{ marginTop: 6 }}>
                <button className="secondary" onClick={() => handleHide(item.id, true)}>
                  Скрыть от семьи
                </button>
                <button className="secondary" onClick={() => handleHide(item.id, false)}>
                  Показать семье
                </button>
              </div>
            </div>
          )}

          {(attachmentsByRecord[item.id]?.length ?? 0) > 0 && (
            <ul style={{ marginTop: 8, paddingLeft: 18 }}>
              {attachmentsByRecord[item.id].map((a) => (
                <li key={a.id}>
                  <button className="secondary" onClick={() => handleOpenAttachment(a.id)}>
                    {a.fileName}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      ))}

      {items.length === 0 && <p className="muted">Записей нет.</p>}
    </div>
  )
}
