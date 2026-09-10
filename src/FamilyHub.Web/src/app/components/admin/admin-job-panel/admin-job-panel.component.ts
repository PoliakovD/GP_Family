import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import {
  AdminApiService,
  EnrichmentFailureReasonValue,
  PipelineJobDetail,
  PipelineJobType,
  SearchCacheSnippet,
  SnippetOverrideItem,
} from '../../../services/admin-api.service';
import { ToastService } from '../../../shared/toast/toast.service';

const POLL_INTERVAL_MS = 3000;

const REASON_LABELS: Record<EnrichmentFailureReasonValue, string> = {
  Legitimacy: 'Не прошла проверка легитимности',
  Plausibility: 'Не прошла проверка правдоподобности',
  NoTrustedSnippets: 'Нет доверенных сниппетов',
  NoSourcesCited: 'Модель не сослалась на источник',
  SummarizerFailed: 'Сбой суммаризации',
  IsolationViolation: 'Подозрение на персональные данные',
  LmStudioUnavailable: 'LM Studio недоступна',
  ProviderFailed: 'Сбой провайдера поиска',
  Unknown: 'Неизвестная ошибка',
};

/**
 * Тело карточки задачи (§7 плана) — монтируется в `app-side-panel` из ДВУХ мест: списка задач
 * (`admin-pipeline`, вкладка «Задачи») и инбокса «Требует внимания» (`admin-attention`), поэтому
 * вынесено отдельным компонентом, а не дублируется. Главный сценарий, ради которого всё это
 * заведено (см. план, Context): «упала задача из-за промаха по доверенным сниппетам» — здесь видно
 * причину, сниппеты кэша с их доменами и можно доверить домен/override конкретного URL и
 * перезапустить ОДНИМ действием, не уходя на вкладку «Обогащение» и обратно.
 *
 * Черновик правок (overrideDraft/trustDraft) копится локально и уходит одним запросом
 * (resolve-and-retry) только по клику «Применить и перезапустить» — один клик = один запрос,
 * а не по запросу на каждый чекбокс (см. cycleSnippetOverride в admin-enrichment для контраста
 * со старым способом, где такого черновика не было).
 */
@Component({
  selector: 'app-admin-job-panel',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './admin-job-panel.component.html',
})
export class AdminJobPanelComponent implements OnChanges, OnDestroy {
  @Input({ required: true }) jobId!: string;
  @Input({ required: true }) jobType!: PipelineJobType;
  /** Эмитится после успешного retry/resolve-and-retry — родитель (список задач/инбокс) обновляет
   * свой список, не дожидаясь закрытия панели. */
  @Output() readonly changed = new EventEmitter<void>();

  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);

  readonly detail = signal<PipelineJobDetail | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);

  /** url -> новое значение override (null — снять override, вернуться к решению по домену). */
  readonly overrideDraft = signal<Record<string, boolean | null>>({});
  readonly trustDraft = signal<Set<string>>(new Set());

  private pollTimer?: ReturnType<typeof setTimeout>;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['jobId'] || changes['jobType']) {
      this.overrideDraft.set({});
      this.trustDraft.set(new Set());
      void this.load();
    }
  }

  ngOnDestroy(): void {
    clearTimeout(this.pollTimer);
  }

  async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.detail.set(await this.api.getPipelineJobDetail(this.jobId, this.jobType));
      this.schedulePollIfRunning();
    } catch {
      this.toast.error('Не удалось загрузить задачу.');
    } finally {
      this.loading.set(false);
    }
  }

  private schedulePollIfRunning(): void {
    clearTimeout(this.pollTimer);
    const status = this.detail()?.status;
    if (status !== 'Pending' && status !== 'Running') return;

    this.pollTimer = setTimeout(async () => {
      try {
        const fresh = await this.api.getPipelineJobDetail(this.jobId, this.jobType);
        const wasRunning = this.detail()?.status === 'Pending' || this.detail()?.status === 'Running';
        this.detail.set(fresh);
        if (wasRunning && fresh.status !== 'Pending' && fresh.status !== 'Running') this.changed.emit();
      } catch {
        // Транзиентная ошибка поллинга — не считаем прогон завершённым, попробуем снова.
      }
      this.schedulePollIfRunning();
    }, POLL_INTERVAL_MS);
  }

  reasonLabel(reason: EnrichmentFailureReasonValue | null): string | null {
    return reason ? (REASON_LABELS[reason] ?? reason) : null;
  }

  /** Итоговое решение с учётом ЕЩЁ НЕ отправленного черновика — предпросмотр того, что реально
   * произойдёт после «Применить и перезапустить», тем же принципом, что серверный Enabled в
   * SearchCacheSnippetDto, только поверх локальных правок. */
  effectiveEnabled(s: SearchCacheSnippet): boolean {
    const draft = this.overrideDraft()[s.url];
    if (draft === true || draft === false) return draft;
    if (draft === null) return s.isTrustedByDomain; // снят override — решает домен
    if (s.domain && this.trustDraft().has(s.domain)) return true;
    return s.enabled;
  }

  /** Тройной клик, как в admin-enrichment: не тронуто → включить → выключить → не тронуто. */
  cycleOverride(s: SearchCacheSnippet): void {
    const current = this.overrideDraft()[s.url];
    const next = current === undefined ? true : current === true ? false : undefined;
    this.overrideDraft.update((d) => {
      const copy = { ...d };
      if (next === undefined) delete copy[s.url];
      else copy[s.url] = next;
      return copy;
    });
  }

  overrideDraftFor(url: string): boolean | null | undefined {
    return this.overrideDraft()[url];
  }

  toggleTrustDomain(domain: string): void {
    this.trustDraft.update((set) => {
      const copy = new Set(set);
      if (copy.has(domain)) copy.delete(domain);
      else copy.add(domain);
      return copy;
    });
  }

  get hasDraftChanges(): boolean {
    return Object.keys(this.overrideDraft()).length > 0 || this.trustDraft().size > 0;
  }

  async applyAndRetry(): Promise<void> {
    const overrides: SnippetOverrideItem[] = Object.entries(this.overrideDraft())
      .map(([url, enabled]) => ({ url, enabled: enabled ?? null }));
    const trustDomains = [...this.trustDraft()];

    this.busy.set(true);
    try {
      await this.api.resolveAndRetryJob(this.jobId, this.jobType, { overrides, trustDomains });
      this.toast.success('Применено, задача перезапущена.');
      this.overrideDraft.set({});
      this.trustDraft.set(new Set());
      await this.load();
      this.changed.emit();
    } catch {
      this.toast.error('Не удалось применить изменения и перезапустить задачу.');
    } finally {
      this.busy.set(false);
    }
  }

  async retryOnly(): Promise<void> {
    this.busy.set(true);
    try {
      await this.api.retryPipelineJob(this.jobId, this.jobType);
      this.toast.success('Задача перезапущена.');
      await this.load();
      this.changed.emit();
    } catch {
      this.toast.error('Не удалось перезапустить задачу.');
    } finally {
      this.busy.set(false);
    }
  }
}
