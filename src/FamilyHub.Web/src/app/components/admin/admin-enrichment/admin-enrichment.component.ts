import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import {
  AdminApiService,
  GlobalSpecimen,
  KbRebuildStatus,
  SearchCacheRow,
  SearchCallDetail,
  SearchCallRow,
  SearchCallStats,
  TrustedDomain,
  WarmupStatus,
  WebSearchCallOutcome,
  WebSearchCallOutcomeValue,
  WebSearchTopic,
  WebSearchTopicValue,
} from '../../../services/admin-api.service';
import { ApiError } from '../../../services/api.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { SidePanelComponent } from '../../../shared/side-panel/side-panel.component';
import { AdminCachePanelComponent } from '../admin-cache-panel/admin-cache-panel.component';

const PAGE_SIZE = 25;
const REBUILD_POLL_INTERVAL_MS = 2000;
const WARMUP_POLL_INTERVAL_MS = 2000;

/**
 * Пересборка enrich-пайплайна — управление доверенными доменами и кэшем сырых результатов поиска
 * (провайдер больше не фильтрует по домену сам, кэш хранит ВСЕ сниппеты, см. class doc
 * AdminEnrichmentEndpoints на бэкенде). Один компонент с двумя вкладками (не вложенные роуты —
 * страница второстепенная, обе вкладки делят выбор темы, отдельные URL не нужны).
 *
 * Состояние в URL (?tab=&topic=&row=, тот же приём, что AdminPipelineComponent) — F5/«назад» не
 * теряют контекст, ссылку на конкретную строку кэша можно переслать.
 */
@Component({
  selector: 'app-admin-enrichment',
  standalone: true,
  imports: [FormsModule, DatePipe, DecimalPipe, SidePanelComponent, AdminCachePanelComponent],
  templateUrl: './admin-enrichment.component.html',
})
export class AdminEnrichmentComponent implements OnInit, OnDestroy {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly WebSearchTopic = WebSearchTopic;
  readonly WebSearchCallOutcome = WebSearchCallOutcome;

  readonly tab = signal<'domains' | 'cache' | 'rebuild' | 'calls' | 'warmup'>('domains');
  readonly topic = signal<WebSearchTopicValue>(WebSearchTopic.Medication);

  readonly domains = signal<TrustedDomain[]>([]);
  readonly newDomain = signal('');
  readonly domainsLoading = signal(true);
  readonly domainsBusy = signal(false);

  readonly cacheRows = signal<SearchCacheRow[]>([]);
  readonly cacheTotal = signal(0);
  readonly cacheQuery = signal('');
  readonly cacheLoading = signal(false);
  /** Открытая строка кэша (§ боковая панель) — null, панель закрыта. */
  readonly openRowId = signal<string | null>(null);
  readonly purgeBusy = signal(false);

  readonly rebuild = signal<KbRebuildStatus | null>(null);
  readonly rebuildLoading = signal(false);
  readonly rebuildBusy = signal(false);
  private rebuildPollTimer?: ReturnType<typeof setTimeout>;

  // --- Вкладка «Вызовы поиска» (аудит платных вызовов, часть 2 плана) ---
  readonly callRows = signal<SearchCallRow[]>([]);
  readonly callsTotal = signal(0);
  readonly callsPage = signal(1);
  readonly callsPageSize = 25;
  readonly callsLoading = signal(false);
  readonly callOutcomeFilter = signal<WebSearchCallOutcomeValue | null>(null);
  readonly callQuery = signal('');
  readonly openCallId = signal<string | null>(null);
  readonly callDetail = signal<SearchCallDetail | null>(null);
  readonly callDetailLoading = signal(false);
  readonly callStats = signal<SearchCallStats | null>(null);
  readonly callStatsLoading = signal(false);

  // --- Вкладка «Прогрев» (грантовый лимит облака — насытить кэш до живого спроса) ---
  readonly warmupNames = signal('');
  readonly warmupSpecimens = signal<GlobalSpecimen[]>([]);
  readonly warmupSpecimensLoading = signal(false);
  readonly warmupSpecimenId = signal<string | null>(null);
  readonly warmupMaxPaidCalls = signal<number | null>(null);
  readonly warmupBusy = signal(false);
  readonly warmupStatus = signal<WarmupStatus | null>(null);
  readonly warmupLoading = signal(false);
  private warmupPollTimer?: ReturnType<typeof setTimeout>;

  ngOnInit(): void {
    const params = this.route.snapshot.queryParamMap;
    const tab = params.get('tab') as 'domains' | 'cache' | 'rebuild' | 'calls' | 'warmup' | null;
    const topicParam = params.get('topic');
    const row = params.get('row');
    const call = params.get('call');

    if (topicParam === '0' || topicParam === '1') this.topic.set(Number(topicParam) as WebSearchTopicValue);
    if (tab) this.tab.set(tab);
    if (row) this.openRowId.set(row);
    if (call) this.openCallId.set(call);

    void this.loadDomains();
    if (this.tab() === 'cache' || row) void this.loadCache();
    if (this.tab() === 'rebuild') void this.loadRebuildStatus();
    if (this.tab() === 'calls' || call) {
      void this.loadCalls();
      void this.loadCallStats();
      if (call) void this.openCallDetail(call);
    }
    if (this.tab() === 'warmup') {
      void this.loadWarmupStatus();
      if (this.topic() === WebSearchTopic.LabAnalyte) void this.loadWarmupSpecimens();
    }
  }

  ngOnDestroy(): void {
    clearTimeout(this.rebuildPollTimer);
    clearTimeout(this.warmupPollTimer);
  }

  private updateQueryParams(extra: Record<string, string | null>): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { tab: this.tab(), topic: String(this.topic()), ...extra },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  selectTab(tab: 'domains' | 'cache' | 'rebuild' | 'calls' | 'warmup'): void {
    this.tab.set(tab);
    this.updateQueryParams({});
    if (tab === 'cache' && this.cacheRows().length === 0) void this.loadCache();
    if (tab === 'rebuild' && this.rebuild() === null) void this.loadRebuildStatus();
    if (tab === 'calls' && this.callRows().length === 0) {
      void this.loadCalls();
      void this.loadCallStats();
    }
    if (tab === 'warmup') {
      if (this.warmupStatus() === null) void this.loadWarmupStatus();
      if (this.topic() === WebSearchTopic.LabAnalyte && this.warmupSpecimens().length === 0) void this.loadWarmupSpecimens();
    }
  }

  async selectTopic(topic: WebSearchTopicValue): Promise<void> {
    this.topic.set(topic);
    this.openRowId.set(null);
    this.updateQueryParams({ row: null });
    await Promise.all([
      this.loadDomains(),
      this.tab() === 'cache' ? this.loadCache() : Promise.resolve(),
      this.tab() === 'calls' ? this.loadCalls(1) : Promise.resolve(),
      this.tab() === 'warmup' && topic === WebSearchTopic.LabAnalyte ? this.loadWarmupSpecimens() : Promise.resolve(),
    ]);
  }

  async loadDomains(): Promise<void> {
    this.domainsLoading.set(true);
    try {
      this.domains.set(await this.api.getTrustedDomains(this.topic()));
    } catch {
      this.toast.error('Не удалось загрузить список доверенных доменов.');
    } finally {
      this.domainsLoading.set(false);
    }
  }

  async addDomain(): Promise<void> {
    const domain = this.newDomain().trim();
    if (!domain) return;

    this.domainsBusy.set(true);
    try {
      await this.api.addTrustedDomain(this.topic(), domain);
      this.newDomain.set('');
      await this.loadDomains();
      this.toast.success('Домен добавлен.');
    } catch {
      this.toast.error('Не удалось добавить домен — возможно, он уже в списке.');
    } finally {
      this.domainsBusy.set(false);
    }
  }

  async toggleDomain(d: TrustedDomain): Promise<void> {
    this.domainsBusy.set(true);
    try {
      await this.api.setTrustedDomainEnabled(d.id, !d.isEnabled);
      await this.loadDomains();
    } catch {
      this.toast.error('Не удалось изменить домен.');
    } finally {
      this.domainsBusy.set(false);
    }
  }

  async deleteDomain(d: TrustedDomain): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Удалить домен?',
      message: `«${d.domain}» будет удалён из списка. Уже закэшированные сниппеты с этого домена останутся в кэше, просто перестанут учитываться по умолчанию.`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;

    this.domainsBusy.set(true);
    try {
      await this.api.deleteTrustedDomain(d.id);
      await this.loadDomains();
    } catch {
      this.toast.error('Не удалось удалить домен.');
    } finally {
      this.domainsBusy.set(false);
    }
  }

  /** Простые стрелки вверх/вниз вместо drag-and-drop — порядок значим только для LabAnalyte
   * (приоритет источника при конфликте норм, см. ReferenceRangeMerger), но UI один на обе темы. */
  async moveDomain(index: number, direction: -1 | 1): Promise<void> {
    const list = [...this.domains()];
    const target = index + direction;
    if (target < 0 || target >= list.length) return;

    [list[index], list[target]] = [list[target], list[index]];
    this.domains.set(list);

    this.domainsBusy.set(true);
    try {
      await this.api.reorderTrustedDomains(this.topic(), list.map((d) => d.id));
    } catch {
      this.toast.error('Не удалось сохранить порядок.');
      await this.loadDomains();
    } finally {
      this.domainsBusy.set(false);
    }
  }

  async loadCache(reset = true): Promise<void> {
    this.cacheLoading.set(true);
    try {
      const skip = reset ? 0 : this.cacheRows().length;
      const page = await this.api.getSearchCache(this.topic(), this.cacheQuery(), skip, PAGE_SIZE);
      this.cacheRows.set(reset ? page.rows : [...this.cacheRows(), ...page.rows]);
      this.cacheTotal.set(page.total);
    } catch {
      this.toast.error('Не удалось загрузить кэш поиска.');
    } finally {
      this.cacheLoading.set(false);
    }
  }

  /** Массовая очистка кэша от строк с неопределённым источником — наследие до пересборки
   * enrich-пайплайна анализов, жёсткий гейт больше не даёт таким строкам появляться заново
   * (см. class doc LabAnalyteSearchCacheService.PurgeUnresolvedSpecimenAsync на бэкенде). */
  async purgeUnresolvedSpecimenCache(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Удалить строки кэша с неопределённым источником?',
      message: 'Эти строки — наследие до пересборки enrich-пайплайна: новые задачи с таким источником больше не ставятся в очередь, поэтому такой кэш никогда не будет прочитан заново.',
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;

    this.purgeBusy.set(true);
    try {
      const { deletedCount } = await this.api.purgeUnresolvedSpecimenSearchCache();
      this.toast.success(`Удалено строк: ${deletedCount}.`);
      await this.loadCache(true);
    } catch {
      this.toast.error('Не удалось очистить кэш.');
    } finally {
      this.purgeBusy.set(false);
    }
  }

  openCacheRow(row: SearchCacheRow): void {
    this.openRowId.set(row.id);
    this.updateQueryParams({ row: row.id });
  }

  closeCacheDetail(): void {
    this.openRowId.set(null);
    this.updateQueryParams({ row: null });
  }

  // --- Пересборка справочника показателей (§4.2 плана) — поллинг статуса, пока прогон Running,
  // тот же приём, что AdminKeysComponent.schedulePollIfRunning. ---

  async loadRebuildStatus(): Promise<void> {
    this.rebuildLoading.set(true);
    try {
      this.rebuild.set(await this.api.getKbRebuildStatus());
      this.scheduleRebuildPollIfRunning();
    } catch {
      this.toast.error('Не удалось загрузить статус пересборки.');
    } finally {
      this.rebuildLoading.set(false);
    }
  }

  private scheduleRebuildPollIfRunning(): void {
    clearTimeout(this.rebuildPollTimer);
    if (this.rebuild()?.status !== 'Running') return;

    this.rebuildPollTimer = setTimeout(async () => {
      try {
        const status = await this.api.getKbRebuildStatus();
        const wasRunning = this.rebuild()?.status === 'Running';
        this.rebuild.set(status);
        if (wasRunning && status.status !== 'Running') {
          this.toast[status.status === 'Completed' ? 'success' : 'error'](
            status.status === 'Completed' ? 'Пересборка справочника завершена.' : `Пересборка упала: ${status.lastError ?? 'см. логи'}`,
          );
        }
      } catch {
        // Транзиентная ошибка поллинга — не считаем прогон завершённым, просто попробуем снова.
      }
      this.scheduleRebuildPollIfRunning();
    }, REBUILD_POLL_INTERVAL_MS);
  }

  // --- Вкладка «Вызовы поиска» (аудит платных вызовов, часть 2 плана) ---

  async loadCalls(page = this.callsPage()): Promise<void> {
    this.callsPage.set(page);
    this.callsLoading.set(true);
    try {
      const outcome = this.callOutcomeFilter();
      const response = await this.api.getSearchCalls(
        { topic: this.topic(), outcome: outcome ?? undefined, query: this.callQuery() || undefined },
        page, this.callsPageSize,
      );
      this.callRows.set(response.rows);
      this.callsTotal.set(response.total);
    } catch {
      this.toast.error('Не удалось загрузить вызовы поиска.');
    } finally {
      this.callsLoading.set(false);
    }
  }

  async loadCallStats(): Promise<void> {
    this.callStatsLoading.set(true);
    try {
      this.callStats.set(await this.api.getSearchCallStats());
    } catch {
      this.toast.error('Не удалось загрузить статистику вызовов поиска.');
    } finally {
      this.callStatsLoading.set(false);
    }
  }

  setCallOutcomeFilter(outcome: WebSearchCallOutcomeValue | null): void {
    this.callOutcomeFilter.set(outcome);
    void this.loadCalls(1);
  }

  async openCallDetail(id: string): Promise<void> {
    this.openCallId.set(id);
    this.updateQueryParams({ call: id });
    this.callDetailLoading.set(true);
    try {
      this.callDetail.set(await this.api.getSearchCallDetail(id));
    } catch {
      this.toast.error('Не удалось загрузить карточку вызова.');
    } finally {
      this.callDetailLoading.set(false);
    }
  }

  closeCallDetail(): void {
    this.openCallId.set(null);
    this.callDetail.set(null);
    this.updateQueryParams({ call: null });
  }

  callsTotalPages(): number {
    return Math.max(1, Math.ceil(this.callsTotal() / this.callsPageSize));
  }

  async startRebuild(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Пересобрать справочник показателей?',
      message: 'Ключи показателей будут пересчитаны новым нормализатором, справочник анализов ' +
        'очищен и наполнен заново поверх уже оплаченного кэша поиска (новых внешних запросов не ' +
        'потребуется). Операция фоновая, панель можно закрыть — прогон продолжится.',
      confirmText: 'Пересобрать',
      danger: true,
    });
    if (!ok) return;

    this.rebuildBusy.set(true);
    try {
      await this.api.startKbRebuild();
      this.toast.success('Пересборка запущена.');
      this.rebuild.set(await this.api.getKbRebuildStatus());
      this.scheduleRebuildPollIfRunning();
    } catch {
      this.toast.error('Не удалось запустить пересборку.');
    } finally {
      this.rebuildBusy.set(false);
    }
  }

  // --- Вкладка «Прогрев» — насытить кэш веб-поиска, пока действует грантовый лимит облака.
  // Делает ТОЛЬКО платный поиск + запись в кэш, без LLM (см. class doc SearchCacheWarmupJob на
  // бэкенде) — справочник наполнится позже бесплатно из уже прогретого кэша. ---

  async loadWarmupSpecimens(): Promise<void> {
    this.warmupSpecimensLoading.set(true);
    try {
      this.warmupSpecimens.set(await this.api.searchSpecimens('', 50));
    } catch {
      this.toast.error('Не удалось загрузить список биоматериалов.');
    } finally {
      this.warmupSpecimensLoading.set(false);
    }
  }

  async loadWarmupStatus(): Promise<void> {
    this.warmupLoading.set(true);
    try {
      this.warmupStatus.set(await this.api.getWarmupStatus());
      this.scheduleWarmupPollIfRunning();
    } catch {
      this.toast.error('Не удалось загрузить статус прогрева.');
    } finally {
      this.warmupLoading.set(false);
    }
  }

  private scheduleWarmupPollIfRunning(): void {
    clearTimeout(this.warmupPollTimer);
    const status = this.warmupStatus()?.status;
    if (status !== 'Running' && status !== 'Paused') return;

    this.warmupPollTimer = setTimeout(async () => {
      try {
        const next = await this.api.getWarmupStatus();
        const wasActive = this.warmupStatus()?.status === 'Running' || this.warmupStatus()?.status === 'Paused';
        this.warmupStatus.set(next);
        if (wasActive && next.status !== 'Running' && next.status !== 'Paused') {
          this.toast[next.status === 'Completed' ? 'success' : 'error'](
            next.status === 'Completed'
              ? `Прогрев завершён: ${next.paidCalls} платных вызовов.`
              : next.status === 'Cancelled'
                ? 'Прогрев остановлен.'
                : `Прогрев упал: ${next.lastError ?? 'см. логи'}`,
          );
        }
      } catch {
        // Транзиентная ошибка поллинга — не считаем прогон завершённым, просто попробуем снова.
      }
      this.scheduleWarmupPollIfRunning();
    }, WARMUP_POLL_INTERVAL_MS);
  }

  async startWarmup(): Promise<void> {
    const names = this.warmupNames().trim();
    if (!names) return;

    if (this.topic() === WebSearchTopic.LabAnalyte && !this.warmupSpecimenId()) {
      this.toast.error('Выберите биоматериал — без него ключ кэша показателей не определён.');
      return;
    }

    const ok = await this.confirm.confirm({
      title: 'Запустить прогрев кэша поиска?',
      message: 'Каждое ещё не закэшированное и отсутствующее в справочнике название — один платный ' +
        'вызов внешнего поиска. Обогащение справочника (ИИ) этим не запускается — только поиск и запись в кэш.',
      confirmText: 'Запустить',
    });
    if (!ok) return;

    this.warmupBusy.set(true);
    try {
      this.warmupStatus.set(await this.api.startWarmup({
        topic: this.topic(),
        specimenKbId: this.topic() === WebSearchTopic.LabAnalyte ? this.warmupSpecimenId() : null,
        names,
        maxPaidCalls: this.warmupMaxPaidCalls(),
      }));
      this.warmupNames.set('');
      this.toast.success('Прогрев запущен.');
      this.scheduleWarmupPollIfRunning();
    } catch (e) {
      const code = e instanceof ApiError ? e.message : null;
      if (code === 'specimen_required') this.toast.error('Для темы «Показатели» нужно выбрать биоматериал.');
      else if (code === 'nothing_to_do') this.toast.error('После разбора список пуст — нечего прогревать.');
      else if (code === 'already_running') {
        this.toast.error('Прогрев уже идёт.');
        await this.loadWarmupStatus();
      } else this.toast.error('Не удалось запустить прогрев.');
    } finally {
      this.warmupBusy.set(false);
    }
  }

  async cancelWarmup(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Остановить прогрев?',
      message: 'Уже потраченные платные вызовы и записанный ими кэш останутся — остановка просто прекращает дальнейшую трату.',
      confirmText: 'Остановить',
      danger: true,
    });
    if (!ok) return;

    this.warmupBusy.set(true);
    try {
      await this.api.cancelWarmup();
      await this.loadWarmupStatus();
    } catch {
      this.toast.error('Не удалось остановить прогрев.');
    } finally {
      this.warmupBusy.set(false);
    }
  }
}
