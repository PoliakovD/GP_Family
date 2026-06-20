import { useCallback, useEffect, useState } from 'react'
import { ApiError, getFamilies } from './api'
import { MemberStatus, type FamilySummary } from './types'
import FamiliesTab from './components/FamiliesTab'
import MedicationsTab from './components/MedicationsTab'
import BirthdaysTab from './components/BirthdaysTab'
import MedicalRecordsTab from './components/MedicalRecordsTab'
import NotificationsTab from './components/NotificationsTab'

type Tab = 'families' | 'medications' | 'birthdays' | 'records' | 'notifications'

const TABS: { id: Tab; label: string }[] = [
  { id: 'families', label: 'Семьи' },
  { id: 'medications', label: 'Аптечка' },
  { id: 'birthdays', label: 'Дни рождения' },
  { id: 'records', label: 'Анализы' },
  { id: 'notifications', label: 'Оповещения' },
]

function App() {
  const [tab, setTab] = useState<Tab>('families')
  const [families, setFamilies] = useState<FamilySummary[]>([])
  const [activeFamilyId, setActiveFamilyId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const refreshFamilies = useCallback(async () => {
    try {
      const result = await getFamilies()
      setFamilies(result)
      setActiveFamilyId((current) => {
        if (current && result.some((f) => f.id === current)) return current
        const firstActive = result.find((f) => f.myStatus === MemberStatus.Active)
        return firstActive?.id ?? null
      })
      setError(null)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не удалось загрузить семьи.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    refreshFamilies()
  }, [refreshFamilies])

  const activeFamilies = families.filter((f) => f.myStatus === MemberStatus.Active)
  const needsFamily = tab === 'medications' || tab === 'birthdays'

  return (
    <>
      <header className="app-header">
        <h1>FamilyHub</h1>
        {activeFamilies.length > 0 && (
          <select
            value={activeFamilyId ?? ''}
            onChange={(e) => setActiveFamilyId(e.target.value || null)}
          >
            {activeFamilies.map((f) => (
              <option key={f.id} value={f.id}>
                {f.name}
              </option>
            ))}
          </select>
        )}
      </header>

      <main className="app-content">
        {error && <div className="error-banner">{error}</div>}
        {loading && <p className="muted">Загрузка…</p>}

        {!loading && tab === 'families' && (
          <FamiliesTab families={families} onChanged={refreshFamilies} />
        )}

        {!loading && needsFamily && activeFamilies.length === 0 && (
          <p className="muted">
            Нет ни одной семьи, в которой вы состоите как активный участник. Создайте семью или
            примите инвайт на вкладке «Семьи».
          </p>
        )}

        {!loading && tab === 'medications' && activeFamilyId && (
          <MedicationsTab familyId={activeFamilyId} />
        )}

        {!loading && tab === 'birthdays' && activeFamilyId && (
          <BirthdaysTab familyId={activeFamilyId} />
        )}

        {!loading && tab === 'records' && <MedicalRecordsTab activeFamilies={activeFamilies} />}

        {!loading && tab === 'notifications' && <NotificationsTab />}
      </main>

      <nav className="tab-bar">
        {TABS.map((t) => (
          <button
            key={t.id}
            className={tab === t.id ? 'active' : ''}
            onClick={() => setTab(t.id)}
          >
            {t.label}
          </button>
        ))}
      </nav>
    </>
  )
}

export default App
