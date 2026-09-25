import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import {
  AdminApiService,
  GlobalSpecimen,
  WarmupStatus,
  WebSearchTopic,
  WebSearchTopicValue,
} from '../../../services/admin-api.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { apiErrorCode } from '../shared/admin-errors';
import { AdminStatusPipe } from '../shared/admin-status.pipe';
import { AdminTopicState } from '../shared/admin-topic-state';
import { TopicSwitchComponent } from '../shared/topic-switch.component';
import { WebSearchBannerComponent } from '../shared/web-search-banner.component';

const WARMUP_POLL_INTERVAL_MS = 2000;

/**
 * «Операции → Прогрев»: насытить кэш веб-поиска, пока действует грантовый лимит облака. Делает
 * ТОЛЬКО платный поиск + запись в кэш, без LLM (см. class doc SearchCacheWarmupJob на бэкенде) —
 * справочник наполнится позже бесплатно из уже прогретого кэша.
 *
 * Поля формы — параметры ОДНОГО запуска, а не сохраняемые настройки, поэтому у страницы нет
 * «Сохранить» и защиты от ухода; запуск переспрашивается (тратит деньги).
 */
@Component({
    selector: 'app-admin-warmup',
    imports: [FormsModule, DatePipe, AdminStatusPipe, TopicSwitchComponent, WebSearchBannerComponent],
    templateUrl: './admin-warmup.component.html'
})
export class AdminWarmupComponent implements OnInit, OnDestroy {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);

  readonly topicState = inject(AdminTopicState);
  readonly WebSearchTopic = WebSearchTopic;

  readonly names = signal('');
  readonly specimens = signal<GlobalSpecimen[]>([]);
  readonly specimensLoading = signal(false);
  readonly specimenId = signal<string | null>(null);
  readonly maxPaidCalls = signal<number | null>(null);
  readonly busy = signal(false);
  readonly status = signal<WarmupStatus | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  private pollTimer?: ReturnType<typeof setTimeout>;

  ngOnInit(): void {
    this.topicState.syncFromRoute(this.route);
    void this.loadStatus();
    if (this.topicState.topic() === WebSearchTopic.LabAnalyte) void this.loadSpecimens();
  }

  ngOnDestroy(): void {
    clearTimeout(this.pollTimer);
  }

  async selectTopic(topic: WebSearchTopicValue): Promise<void> {
    this.topicState.select(topic, this.route);
    if (topic === WebSearchTopic.LabAnalyte && this.specimens().length === 0) await this.loadSpecimens();
  }

  async loadSpecimens(): Promise<void> {
    this.specimensLoading.set(true);
    try {
      this.specimens.set(await this.api.searchSpecimens('', 50));
    } catch {
      this.toast.error('Не удалось загрузить список биоматериалов.');
    } finally {
      this.specimensLoading.set(false);
    }
  }

  async loadStatus(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.status.set(await this.api.getWarmupStatus());
      this.schedulePollIfRunning();
    } catch {
      this.error.set('Не удалось загрузить статус прогрева.');
    } finally {
      this.loading.set(false);
    }
  }

  private schedulePollIfRunning(): void {
    clearTimeout(this.pollTimer);
    const status = this.status()?.status;
    if (status !== 'Running' && status !== 'Paused') return;

    this.pollTimer = setTimeout(async () => {
      try {
        const next = await this.api.getWarmupStatus();
        const wasActive = this.status()?.status === 'Running' || this.status()?.status === 'Paused';
        this.status.set(next);
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
      this.schedulePollIfRunning();
    }, WARMUP_POLL_INTERVAL_MS);
  }

  async start(): Promise<void> {
    const names = this.names().trim();
    if (!names) return;

    const topic = this.topicState.topic();
    if (topic === WebSearchTopic.LabAnalyte && !this.specimenId()) {
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

    this.busy.set(true);
    try {
      this.status.set(await this.api.startWarmup({
        topic,
        specimenKbId: topic === WebSearchTopic.LabAnalyte ? this.specimenId() : null,
        names,
        maxPaidCalls: this.maxPaidCalls(),
      }));
      this.names.set('');
      this.toast.success('Прогрев запущен.');
      this.schedulePollIfRunning();
    } catch (e) {
      const code = apiErrorCode(e);
      if (code === 'specimen_required') this.toast.error('Для темы «Анализы» нужно выбрать биоматериал.');
      else if (code === 'nothing_to_do') this.toast.error('После разбора список пуст — нечего прогревать.');
      else if (code === 'already_running') {
        this.toast.error('Прогрев уже идёт.');
        await this.loadStatus();
      } else this.toast.error('Не удалось запустить прогрев.');
    } finally {
      this.busy.set(false);
    }
  }

  async cancel(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Остановить прогрев?',
      message: 'Уже потраченные платные вызовы и записанный ими кэш останутся — остановка просто прекращает дальнейшую трату.',
      confirmText: 'Остановить',
      danger: true,
    });
    if (!ok) return;

    this.busy.set(true);
    try {
      await this.api.cancelWarmup();
      await this.loadStatus();
    } catch {
      this.toast.error('Не удалось остановить прогрев.');
    } finally {
      this.busy.set(false);
    }
  }
}
