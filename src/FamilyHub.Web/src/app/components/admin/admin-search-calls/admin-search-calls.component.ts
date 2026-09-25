import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import {
  AdminApiService,
  SearchCallDetail,
  SearchCallRow,
  SearchCallStats,
  WebSearchCallOutcome,
  WebSearchCallOutcomeValue,
  WebSearchTopicValue,
} from '../../../services/admin-api.service';
import { SidePanelComponent } from '../../../shared/side-panel/side-panel.component';
import { ToastService } from '../../../shared/toast/toast.service';
import { OUTCOME_OPTIONS, outcomeKeyLabel, outcomeLabel } from '../shared/admin-labels';
import { AdminTopicState } from '../shared/admin-topic-state';
import { TopicSwitchComponent } from '../shared/topic-switch.component';
import { WebSearchBannerComponent } from '../shared/web-search-banner.component';

/**
 * «Операции → Журнал вызовов»: полный аудит обращений к платному внешнему поиску (Yandex/Brave) —
 * включая кэш-хиты (не платные, но видны здесь же: так можно проверить, что кэш реально работает),
 * плюс сводка за 30 дней. Только чтение — вентиль платного поиска управляется в
 * «Настройки → Веб-поиск» (здесь — баннер, если он закрыт).
 *
 * Состояние в URL (?topic=&call=) — ссылку на конкретный вызов можно переслать.
 */
@Component({
    selector: 'app-admin-search-calls',
    imports: [FormsModule, DatePipe, DecimalPipe, SidePanelComponent, TopicSwitchComponent, WebSearchBannerComponent],
    templateUrl: './admin-search-calls.component.html'
})
export class AdminSearchCallsComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);

  readonly topicState = inject(AdminTopicState);
  readonly WebSearchCallOutcome = WebSearchCallOutcome;
  readonly outcomeOptions = OUTCOME_OPTIONS;
  readonly outcomeLabel = outcomeLabel;
  readonly outcomeKeyLabel = outcomeKeyLabel;

  readonly rows = signal<SearchCallRow[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly pageSize = 25;
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly outcomeFilter = signal<WebSearchCallOutcomeValue | null>(null);
  readonly query = signal('');
  readonly openCallId = signal<string | null>(null);
  readonly detail = signal<SearchCallDetail | null>(null);
  readonly detailLoading = signal(false);
  readonly stats = signal<SearchCallStats | null>(null);
  readonly statsLoading = signal(false);
  readonly statsError = signal<string | null>(null);

  ngOnInit(): void {
    this.topicState.syncFromRoute(this.route);
    const call = this.route.snapshot.queryParamMap.get('call');

    void this.load();
    void this.loadStats();
    if (call) void this.openDetail(call);
  }

  async selectTopic(topic: WebSearchTopicValue): Promise<void> {
    this.topicState.select(topic, this.route);
    await this.load(1);
  }

  async load(page = this.page()): Promise<void> {
    this.page.set(page);
    this.loading.set(true);
    this.error.set(null);
    try {
      const outcome = this.outcomeFilter();
      const response = await this.api.getSearchCalls(
        { topic: this.topicState.topic(), outcome: outcome ?? undefined, query: this.query() || undefined },
        page, this.pageSize,
      );
      this.rows.set(response.rows);
      this.total.set(response.total);
    } catch {
      this.error.set('Не удалось загрузить вызовы поиска.');
    } finally {
      this.loading.set(false);
    }
  }

  async loadStats(): Promise<void> {
    this.statsLoading.set(true);
    this.statsError.set(null);
    try {
      this.stats.set(await this.api.getSearchCallStats());
    } catch {
      this.statsError.set('Не удалось загрузить статистику вызовов поиска.');
    } finally {
      this.statsLoading.set(false);
    }
  }

  setOutcomeFilter(outcome: WebSearchCallOutcomeValue | null): void {
    this.outcomeFilter.set(outcome);
    void this.load(1);
  }

  async openDetail(id: string): Promise<void> {
    this.openCallId.set(id);
    this.topicState.select(this.topicState.topic(), this.route, { call: id });
    this.detailLoading.set(true);
    try {
      this.detail.set(await this.api.getSearchCallDetail(id));
    } catch {
      this.toast.error('Не удалось загрузить карточку вызова.');
    } finally {
      this.detailLoading.set(false);
    }
  }

  closeDetail(): void {
    this.openCallId.set(null);
    this.detail.set(null);
    this.topicState.select(this.topicState.topic(), this.route, { call: null });
  }

  totalPages(): number {
    return Math.max(1, Math.ceil(this.total() / this.pageSize));
  }
}
