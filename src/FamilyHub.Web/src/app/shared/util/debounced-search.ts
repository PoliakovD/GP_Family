// Общая логика «печатай -> debounce -> запрос -> отбрось устаревшие ответы» — вынесена из
// исходного SearchComponent (этап 3, /api/search), теперь используется везде, где есть серверный
// поисковый источник: Главная (все три источника), Аптечка (types=medication), Анализы (types=record).
// Для разделов без серверного источника (Дни рождения) — см. local-filter.ts вместо этого класса.

/** Синхронизировано с SearchService.MinQueryLength на бэкенде — короче не дёргаем API вовсе. */
export const SEARCH_MIN_QUERY_LENGTH = 2;
const DEBOUNCE_MS = 300;

export class DebouncedSearch<T> {
  query = '';
  items: T[] = [];
  loading = false;
  error: string | null = null;
  /** true после первого выполненного запроса — отличает «ещё не искали» от «искали, пусто». */
  searched = false;

  private debounceTimer: ReturnType<typeof setTimeout> | null = null;
  /** Отбрасываем ответы устаревших запросов, если пользователь печатает быстрее ответа сети. */
  private requestSeq = 0;

  constructor(
    private readonly fetch: (query: string) => Promise<T[]>,
    private readonly describeError: (err: unknown) => string = () => 'Не удалось выполнить поиск.',
  ) {}

  onQueryChange(): void {
    if (this.debounceTimer) clearTimeout(this.debounceTimer);

    const q = this.query.trim();
    if (q.length < SEARCH_MIN_QUERY_LENGTH) {
      this.requestSeq++; // инвалидируем в полёте ответ на предыдущий, более длинный запрос
      this.items = [];
      this.searched = false;
      this.error = null;
      this.loading = false;
      return;
    }

    this.debounceTimer = setTimeout(() => void this.run(q), DEBOUNCE_MS);
  }

  /** Немедленный перезапуск без debounce — для явного действия пользователя (клик по чипу-фильтру и т.п.). */
  rerun(): void {
    if (this.debounceTimer) clearTimeout(this.debounceTimer);
    const q = this.query.trim();
    if (q.length < SEARCH_MIN_QUERY_LENGTH) return;
    void this.run(q);
  }

  /** Полный сброс — например, после клика по результату, когда список больше не нужен. */
  reset(): void {
    if (this.debounceTimer) clearTimeout(this.debounceTimer);
    this.requestSeq++;
    this.query = '';
    this.items = [];
    this.searched = false;
    this.error = null;
    this.loading = false;
  }

  private async run(q: string): Promise<void> {
    const seq = ++this.requestSeq;
    this.loading = true;
    try {
      const items = await this.fetch(q);
      if (seq !== this.requestSeq) return;
      this.items = items;
      this.error = null;
    } catch (err) {
      if (seq !== this.requestSeq) return;
      this.error = this.describeError(err);
    } finally {
      if (seq === this.requestSeq) {
        this.loading = false;
        this.searched = true;
      }
    }
  }
}
