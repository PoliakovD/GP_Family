import { WebSearchCallOutcome, WebSearchCallOutcomeValue } from '../../../services/admin-api.service';

/**
 * Русские подписи для статусов и исходов, которые бэкенд отдаёт английскими идентификаторами.
 * Раньше их выводили как есть ({{ run.status }}) — админка смешивала два языка.
 *
 * Неизвестное значение показываем как пришло: лучше английский идентификатор, чем пустая ячейка
 * или молчаливая подмена.
 */
const STATUS_LABELS: Record<string, string> = {
  // Задачи конвейеров (EnrichmentJobStatus)
  Pending: 'В очереди',
  Running: 'Выполняется',
  Completed: 'Завершена',
  Failed: 'Ошибка',
  Skipped: 'Пропущена',
  Deferred: 'Отложена',
  // Прогоны (перешифровка, пересборка, прогрев)
  Paused: 'На паузе',
  Cancelled: 'Остановлен',
  // Ротация учёток приложения (CredentialRotationStatus)
  AwaitingDeploy: 'Ждёт деплоя',
  Activated: 'Применена, старая действует',
  Revoked: 'Завершена, старая отозвана',
  Superseded: 'Заменена новой',
};

export function statusLabel(status: string | null | undefined): string {
  if (!status) return '—';
  return STATUS_LABELS[status] ?? status;
}

/** Статусы для фильтра списка задач — порядок как в жизненном цикле задачи. */
export const JOB_STATUS_OPTIONS: { value: string; label: string }[] = [
  { value: 'Pending', label: statusLabel('Pending') },
  { value: 'Running', label: statusLabel('Running') },
  { value: 'Completed', label: statusLabel('Completed') },
  { value: 'Failed', label: statusLabel('Failed') },
  { value: 'Skipped', label: statusLabel('Skipped') },
  { value: 'Deferred', label: statusLabel('Deferred') },
];

const OUTCOME_LABELS: Record<WebSearchCallOutcomeValue, string> = {
  [WebSearchCallOutcome.Ok]: 'Успешно',
  [WebSearchCallOutcome.Empty]: 'Пусто',
  [WebSearchCallOutcome.Rejected]: 'Отклонён',
  [WebSearchCallOutcome.NoUsedSources]: 'Без источников',
  [WebSearchCallOutcome.HttpError]: 'Ошибка HTTP',
  [WebSearchCallOutcome.Timeout]: 'Таймаут',
  [WebSearchCallOutcome.CacheHit]: 'Из кэша',
};

export function outcomeLabel(outcome: WebSearchCallOutcomeValue): string {
  return OUTCOME_LABELS[outcome];
}

/** Подпись исхода по ИМЕНИ enum'а (`Ok`, `CacheHit` …) — так сводка вызовов группирует исходы
 * (`Outcome.ToString()` на бэкенде). Неизвестное имя показываем как пришло. */
export function outcomeKeyLabel(key: string): string {
  const value = (WebSearchCallOutcome as Record<string, WebSearchCallOutcomeValue>)[key];
  return value === undefined ? key : outcomeLabel(value);
}

/** Исходы для фильтра журнала вызовов, в порядке enum. */
export const OUTCOME_OPTIONS: { value: WebSearchCallOutcomeValue; label: string }[] = (
  Object.values(WebSearchCallOutcome) as WebSearchCallOutcomeValue[]
).map((value) => ({ value, label: outcomeLabel(value) }));
