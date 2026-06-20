import { useEffect, useState, type FormEvent } from 'react'
import { ApiError, createBirthday, deleteBirthday, getBirthdays, updateBirthday } from '../api'
import type { Birthday } from '../types'

interface Props {
  familyId: string
}

const emptyForm = { personName: '', date: '' }

export default function BirthdaysTab({ familyId }: Props) {
  const [items, setItems] = useState<Birthday[]>([])
  const [form, setForm] = useState(emptyForm)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function refresh() {
    try {
      setItems(await getBirthdays(familyId))
      setError(null)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не удалось загрузить дни рождения.')
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
    if (!form.personName.trim() || !form.date) return
    const input = { personName: form.personName.trim(), date: form.date }
    try {
      if (editingId) {
        await updateBirthday(editingId, input)
      } else {
        await createBirthday(familyId, input)
      }
      setForm(emptyForm)
      setEditingId(null)
      await refresh()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не удалось сохранить запись.')
    }
  }

  function startEdit(item: Birthday) {
    setEditingId(item.id)
    setForm({ personName: item.personName, date: item.date })
  }

  async function handleDelete(id: string) {
    try {
      await deleteBirthday(id)
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
          placeholder="Имя"
          value={form.personName}
          onChange={(e) => setForm({ ...form, personName: e.target.value })}
        />
        <input
          type="date"
          value={form.date}
          onChange={(e) => setForm({ ...form, date: e.target.value })}
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
            <strong>{item.personName}</strong>
            <span className="muted">{item.date}</span>
          </div>
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

      {items.length === 0 && <p className="muted">Дни рождения не добавлены.</p>}
    </div>
  )
}
