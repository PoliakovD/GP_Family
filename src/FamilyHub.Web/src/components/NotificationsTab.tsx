import { useEffect, useState } from 'react'
import { ApiError, getNotifications, markNotificationRead } from '../api'
import { NotificationType, type AppNotification } from '../types'

const TYPE_LABEL: Record<number, string> = {
  [NotificationType.MedicationExpiringSoon]: 'Срок годности скоро истекает',
  [NotificationType.MedicationExpired]: 'Срок годности истёк',
  [NotificationType.BirthdayUpcoming]: 'Скоро день рождения',
}

export default function NotificationsTab() {
  const [items, setItems] = useState<AppNotification[]>([])
  const [unreadOnly, setUnreadOnly] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function refresh() {
    try {
      setItems(await getNotifications(unreadOnly))
      setError(null)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не удалось загрузить оповещения.')
    }
  }

  useEffect(() => {
    refresh()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [unreadOnly])

  async function handleMarkRead(id: string) {
    try {
      await markNotificationRead(id)
      await refresh()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не удалось отметить как прочитанное.')
    }
  }

  return (
    <div>
      {error && <div className="error-banner">{error}</div>}

      <label className="row" style={{ marginBottom: 10 }}>
        <span>Только непрочитанные</span>
        <input
          type="checkbox"
          checked={unreadOnly}
          onChange={(e) => setUnreadOnly(e.target.checked)}
        />
      </label>

      {items.map((n) => (
        <div className="card" key={n.id}>
          <div className="row">
            <strong>{n.title}</strong>
            {!n.isRead && <span className="badge unread">новое</span>}
          </div>
          <p className="muted">{TYPE_LABEL[n.type] ?? 'Оповещение'}</p>
          <p>{n.body}</p>
          {!n.isRead && (
            <button className="secondary" onClick={() => handleMarkRead(n.id)}>
              Отметить прочитанным
            </button>
          )}
        </div>
      ))}

      {items.length === 0 && <p className="muted">Оповещений нет.</p>}
    </div>
  )
}
