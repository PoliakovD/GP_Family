import { Component, OnInit, inject, signal, viewChild } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import {
  AdminApiService,
  SearchCacheRow,
  WebSearchTopic,
  WebSearchTopicValue,
} from '../../../services/admin-api.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { SidePanelComponent } from '../../../shared/side-panel/side-panel.component';
import { ToastService } from '../../../shared/toast/toast.service';
import { AdminCachePanelComponent, SearchCacheRowPatch } from '../admin-cache-panel/admin-cache-panel.component';
import { AdminTopicState } from '../shared/admin-topic-state';
import { TopicSwitchComponent } from '../shared/topic-switch.component';

const PAGE_SIZE = 25;

/**
 * «Операции → Кэш поиска»: кэш сырых результатов платного поиска. Хранит ВСЕ сниппеты, не только
 * доверенные (провайдер больше не фильтрует по домену сам, см. class doc AdminEnrichmentEndpoints
 * на бэкенде) — можно поменять список доменов или точечно включить/выключить URL без нового
 * платного запроса.
 *
 * Название строки — для людей («СРБ»), под ним мелко ключ кэша (свёрнутый, по нему задачи находят строку).
 * У показателей — колонка единиц и фильтр «единицы не определены».
 *
 * Состояние в URL (?topic=&row=&units=undetermined) — F5/«назад» не теряют контекст, ссылку можно переслать.
 */
@Component({
    selector: 'app-admin-search-cache',
    imports: [FormsModule, DatePipe, SidePanelComponent, AdminCachePanelComponent, TopicSwitchComponent],
    templateUrl: './admin-search-cache.component.html',
    styleUrl: './admin-search-cache.component.scss'
})
export class AdminSearchCacheComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);

  readonly topicState = inject(AdminTopicState);
  readonly WebSearchTopic = WebSearchTopic;

  readonly rows = signal<SearchCacheRow[]>([]);
  readonly total = signal(0);
  readonly query = signal('');
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  /** Открытая строка кэша (боковая панель) — null, панель закрыта. */
  readonly openRowId = signal<string | null>(null);
  readonly purgeBusy = signal(false);
  /** Только строки, где единицы ещё не определены (показатели). */
  readonly unitsUndetermined = signal(false);

  private readonly panel = viewChild(AdminCachePanelComponent);

  ngOnInit(): void {
    this.topicState.syncFromRoute(this.route);
    const row = this.route.snapshot.queryParamMap.get('row');
    if (row) this.openRowId.set(row);
    this.unitsUndetermined.set(this.route.snapshot.queryParamMap.get('units') === 'undetermined');

    void this.load();
  }

  async selectTopic(topic: WebSearchTopicValue): Promise<void> {
    this.openRowId.set(null);
    this.topicState.select(topic, this.route, { row: null });
    await this.load();
  }

  async load(reset = true): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const skip = reset ? 0 : this.rows().length;
      const page = await this.api.getSearchCache(
        this.topicState.topic(), this.query(), skip, PAGE_SIZE,
        this.topicState.topic() === WebSearchTopic.LabAnalyte && this.unitsUndetermined());
      this.rows.set(reset ? page.rows : [...this.rows(), ...page.rows]);
      this.total.set(page.total);
    } catch {
      this.error.set('Не удалось загрузить кэш поиска.');
    } finally {
      this.loading.set(false);
    }
  }

  /** Массовая очистка кэша от строк с неопределённым источником — наследие до пересборки
   * enrich-пайплайна анализов, жёсткий гейт больше не даёт таким строкам появляться заново
   * (см. class doc LabAnalyteSearchCacheService.PurgeUnresolvedSpecimenAsync на бэкенде). */
  async purgeUnresolvedSpecimen(): Promise<void> {
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
      await this.load(true);
    } catch {
      this.toast.error('Не удалось очистить кэш.');
    } finally {
      this.purgeBusy.set(false);
    }
  }

  async toggleUnitsUndetermined(value: boolean): Promise<void> {
    this.unitsUndetermined.set(value);
    this.topicState.select(this.topicState.topic(), this.route, { units: value ? 'undetermined' : null });
    await this.load(true);
  }

  /** Строка после сохранения в панели — обновляем на месте, не сбрасывая подгруженные страницы. */
  applyPatch(patch: SearchCacheRowPatch): void {
    this.rows.update((rows) => rows.map((r) => (r.id === patch.id
      ? { ...r, displayName: patch.displayName, units: patch.units, provider: patch.provider, snippetCount: patch.snippetCount }
      : r)));
  }

  removeRow(id: string): void {
    this.rows.update((rows) => rows.filter((r) => r.id !== id));
    this.total.update((t) => Math.max(0, t - 1));
  }

  /** Прошёл минимальный интервал обновления — следующая задача по этому названию может сделать платный поиск. */
  isStale(row: SearchCacheRow): boolean {
    return new Date(row.canBeUpdatedAfter).getTime() <= Date.now();
  }

  openRow(row: SearchCacheRow): void {
    this.openRowId.set(row.id);
    this.topicState.select(this.topicState.topic(), this.route, { row: row.id });
  }

  /** Закрытие панели с несохранёнными правками (название, единицы, сниппеты) — только после подтверждения. */
  async requestClose(): Promise<void> {
    if (this.panel()?.dirty()) {
      const ok = await this.confirm.confirm({
        title: 'Закрыть без сохранения?',
        message: 'Изменения в этой строке кэша не сохранены и пропадут.',
        confirmText: 'Закрыть',
        danger: true,
      });
      if (!ok) return;
    }
    this.closeDetail();
  }

  closeDetail(): void {
    this.openRowId.set(null);
    this.topicState.select(this.topicState.topic(), this.route, { row: null });
  }
}
