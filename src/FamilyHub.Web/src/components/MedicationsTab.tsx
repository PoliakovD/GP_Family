import { useEffect, useState, type FormEvent } from 'react'
import {
  ApiError,
  createMedication,
  deleteMedication,
  getMedications,
  updateMedication,
} from '../api'
import type { Medication } from '../types'

interface Props {
  familyId: string
}

const emptyForm = { name: '', instructions: '', expiryDate: '', quantity: 1 }

export default function MedicationsTab({ familyId }: Props) {
  const [items, setItems] = useState<Medication[]>([])
  const [form, setForm] = useState(emptyForm)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function refresh() {
    try {
      setItems(await getMedications(familyId))
      setError(null)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не удалось загрузить аптечку.')
    }
  }

  useEffect(() => {
    refresh()
    setForm(emptyForm)
    setEditingId(null)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [familyId])

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    if (!form.name.trim()) return
    const input = {
      name: form.name.trim(),
      instructions: form.instructions.trim() || null,
      expiryDate: form.expiryDate || null,
      quantity: Number(form.quantity) || 0,
    }
    try {
      if (editingId) {
        await updateMedication(editingId, input)
      } else {
        await createMedication(familyId, input)
      }
      setForm(emptyForm)
      setEditingId(null)
      await refresh()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не удалось сохранить запись.')
    }
  }

  function startEdit(item: Medication) {
    setEditingId(item.id)
    setForm({
      name: item.name,
      instructions: item.instructions ?? '',
      expiryDate: item.expiryDate ?? '',
      quantity: item.quantity,
    })
  }

  async function handleDelete(id: string) {
    try {
      await deleteMedication(id)
      await refresh()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не удалось удалить запись.')
    }
  }

  return (
    <div>
      {error && <div className="error-banner">{error}</div>}

      <form className="stack" onSubmit={handleSubmit}>
        <input
          placeholder="Название препарата"
          value={form.name}
          onChange={(e) => setForm({ ...form, name: e.target.value })}
        />
        <input
          placeholder="Инструкция (необязательно)"
          value={form.instructions}
          onChange={(e) => setForm({ ...form, instructions: e.target.value })}
        />
        <input
          type="date"
          value={form.expiryDate}
          onChange={(e) => setForm({ ...form, expiryDate: e.target.value })}
        />
        <input
          type="number"
          min={0}
          placeholder="Количество"
          value={form.quantity}
          onChange={(e) => setForm({ ...form, quantity: Number(e.target.value) })}
        />
        <button className="primary" type="submit">
          {editingId ? 'Сохранить' : 'Добавить'}
        </button>
        {editingId && (
          <button
            type="button"
            className="secondary"
            onClick={() => {
              setEditingId(null)
              setForm(emptyForm)
            }}
          >
            Отмена
          </button>
        )}
      </form>

      {items.map((item) => (
        <div className="card" key={item.id}>
          <div className="row">
            <strong>{item.name}</strong>
            <span className="muted">кол-во: {item.quantity}</span>
          </div>
          {item.instructions && <p className="muted">{item.instructions}</p>}
          {item.expiryDate && <p className="muted">Срок годности: {item.expiryDate}</p>}
          <div className="row" style={{ marginTop: 6 }}>
            <button className="secondary" onClick={() => startEdit(item)}>
              Изменить
            </button>
            <button className="secondary danger" onClick={() => handleDelete(item.id)}>
              Удалить
            </button>
          </div>
        </div>
      ))}

      {items.length === 0 && <p className="muted">Аптечка пуста.</p>}
    </div>
  )
}
