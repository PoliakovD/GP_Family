import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { DatePipe, JsonPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import {
  AdminApiService,
  EnrichmentFailureReasonValue,
  LmStudioAvailableModels,
  LmStudioReasoning,
  PipelineJob,
  PipelineJobType,
  PipelineStep,
  PromptSlot,
  PromptVersion,
} from '../../../services/admin-api.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { SidePanelComponent } from '../../../shared/side-panel/side-panel.component';
import { AdminJobPanelComponent } from '../admin-job-panel/admin-job-panel.component';

const JOB_POLL_INTERVAL_MS = 3000;

const JOB_TYPES: { value: PipelineJobType; label: string }[] = [
  { value: 'lab-analyte', label: 'Обогащение показателей' },
  { value: 'medication', label: 'Обогащение медикаментов' },
  { value: 'visit-medication', label: 'Медикаменты из заключений' },
  { value: 'extraction', label: 'Извлечение из документов' },
];

/**
 * Управление enrich-пайплайном из админки (§2 плана): вкл/выкл необязательных шагов, версии
 * промптов (создание/откат — ничего не удаляется), dry-run без записи в справочник, листинг
 * задач всех четырёх конвейеров. Реордер шагов сюда не входит — реальная последовательность
 * зашита в процессорах (жёсткие зависимости между шагами одного прогона), из админки доступно
 * только вкл/выкл (см. class doc AdminPipelineEndpoints на бэкенде).
 */
@Component({
  selector: 'app-admin-pipeline',
  standalone: true,
  imports: [FormsModule, DatePipe, JsonPipe, SidePanelComponent, AdminJobPanelComponent],
  templateUrl: './admin-pipeline.component.html',
})
export class AdminPipelineComponent implements OnInit, OnDestroy {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly jobTypes = JOB_TYPES;

  readonly tab = signal<'steps' | 'prompts' | 'jobs' | 'lmstudio'>('steps');

  readonly steps = signal<PipelineStep[]>([]);
  readonly stepsLoading = signal(true);
  readonly stepsBusy = signal(false);

  readonly promptSlots = signal<PromptSlot[]>([]);
  readonly promptsLoading = signal(true);
  readonly selectedPromptKey = signal<string | null>(null);
  readonly promptVersions = signal<PromptVersion[]>([]);
  readonly promptVersionsLoading = signal(false);
  readonly editorBody = signal('');
  readonly editorNote = signal('');
  readonly editorBusy = signal(false);

  readonly dryRunUserText = signal('');
  readonly dryRunBusy = signal(false);
  readonly dryRunResult = signal<{ success: boolean; error: string | null; payload: Record<string, unknown> | null } | null>(null);

  readonly jobType = signal<PipelineJobType>('lab-analyte');
  readonly jobStatus = signal<string>('');
  readonly jobReason = signal<EnrichmentFailureReasonValue | null>(null);
  readonly jobs = signal<PipelineJob[]>([]);
  readonly jobsTotal = signal(0);
  readonly jobsLoading = signal(false);
  readonly jobsBusy = signal(false);
  readonly selectedJobIds = signal<Set<string>>(new Set());

  /** Карточка задачи (§7 плана) — открывается из этой же таблицы задач и из инбокса «Требует
   * внимания» (через query-параметр job, см. ngOnInit). null — панель закрыта. */
  readonly openJobId = signal<string | null>(null);
  private jobPollTimer?: ReturnType<typeof setTimeout>;

  readonly lmStudioActiveModel = signal<string | null>(null);
  readonly lmStudioFallbackModel = signal('');
  readonly lmStudioAvailable = signal<LmStudioAvailableModels | null>(null);
  readonly lmStudioSelectedModel = signal('');
  readonly lmStudioLoading = signal(false);
  readonly lmStudioBusy = signal(false);

  /** Уровень "размышлений" — отдельные сигналы от модели выше: независимая загрузка/сохранение,
   * ошибка сохранения одного не должна блокировать кнопку у другого. */
  readonly lmStudioReasoningActive = signal<LmStudioReasoning | null>(null);
  readonly lmStudioReasoningFallback = signal<LmStudioReasoning | null>(null);
  readonly lmStudioReasoningSelected = signal<LmStudioReasoning>(LmStudioReasoning.None);
  readonly lmStudioReasoningLoading = signal(false);
  readonly lmStudioReasoningBusy = signal(false);

  readonly reasoningOptions: { value: LmStudioReasoning; label: string }[] = [
    { value: LmStudioReasoning.None, label: 'Без рассуждений (быстрее)' },
    { value: LmStudioReasoning.Minimal, label: 'Минимальные' },
    { value: LmStudioReasoning.Medium, label: 'Умеренные' },
    { value: LmStudioReasoning.Maximum, label: 'Максимальные (медленнее)' },
  ];

  reasoningLabel(value: LmStudioReasoning): string {
    return this.reasoningOptions.find((o) => o.value === value)?.label ?? String(value);
  }

  ngOnInit(): void {
    // Состояние в URL (§8 плана) — переход из «Требует внимания» (?tab=jobs&type=&status=&reason=)
    // и прямые ссылки на конкретную задачу (?job=<guid>) открывают нужный вид без ручных кликов;
    // F5/«назад» сохраняют контекст. Читается один раз при входе — дальнейшая навигация внутри
    // компонента сама пишет параметры обратно (см. updateQueryParams).
    const params = this.route.snapshot.queryParamMap;
    const tab = params.get('tab');
    const type = params.get('type') as PipelineJobType | null;
    const status = params.get('status');
    const reason = params.get('reason') as EnrichmentFailureReasonValue | null;
    const job = params.get('job');

    if (type) this.jobType.set(type);
    if (status) this.jobStatus.set(status);
    if (reason) this.jobReason.set(reason);

    if (tab === 'jobs' || type || status || reason || job) {
      this.tab.set('jobs');
      void this.loadJobs();
    } else {
      void this.loadSteps();
    }

    if (job) this.openJobId.set(job);
  }

  ngOnDestroy(): void {
    clearTimeout(this.jobPollTimer);
  }

  private updateQueryParams(extra: Record<string, string | null>): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { tab: this.tab(), ...extra },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  selectTab(tab: 'steps' | 'prompts' | 'jobs' | 'lmstudio'): void {
    this.tab.set(tab);
    this.updateQueryParams({});
    if (tab === 'prompts' && this.promptSlots().length === 0) void this.loadPrompts();
    if (tab === 'jobs' && this.jobs().length === 0) void this.loadJobs();
    if (tab === 'lmstudio' && this.lmStudioFallbackModel() === '') void this.loadLmStudioModel();
    if (tab === 'lmstudio' && this.lmStudioReasoningFallback() === null) void this.loadLmStudioReasoning();
  }

  // --- Шаги ---

  async loadSteps(): Promise<void> {
    this.stepsLoading.set(true);
    try {
      this.steps.set(await this.api.getPipelineSteps());
    } catch {
      this.toast.error('Не удалось загрузить шаги пайплайна.');
    } finally {
      this.stepsLoading.set(false);
    }
  }

  async toggleStep(step: PipelineStep): Promise<void> {
    if (step.isMandatory) return;
    this.stepsBusy.set(true);
    try {
      await this.api.setStepEnabled(step.pipelineKey, step.stepKey, !step.isEnabled);
      await this.loadSteps();
    } catch {
      this.toast.error('Не удалось изменить шаг.');
    } finally {
      this.stepsBusy.set(false);
    }
  }

  // --- Промпты ---

  async loadPrompts(): Promise<void> {
    this.promptsLoading.set(true);
    try {
      this.promptSlots.set(await this.api.getPromptSlots());
    } catch {
      this.toast.error('Не удалось загрузить список промптов.');
    } finally {
      this.promptsLoading.set(false);
    }
  }

  async selectPrompt(key: string): Promise<void> {
    this.selectedPromptKey.set(key);
    this.dryRunResult.set(null);
    this.promptVersionsLoading.set(true);
    try {
      const versions = await this.api.getPromptVersions(key);
      this.promptVersions.set(versions);
      const active = versions.find((v) => v.isActive);
      this.editorBody.set(active?.body ?? '');
      this.editorNote.set('');
    } catch {
      this.toast.error('Не удалось загрузить версии промпта.');
    } finally {
      this.promptVersionsLoading.set(false);
    }
  }

  async saveNewVersion(): Promise<void> {
    const key = this.selectedPromptKey();
    if (!key || !this.editorBody().trim()) return;

    this.editorBusy.set(true);
    try {
      await this.api.createPromptVersion(key, this.editorBody(), this.editorNote().trim() || null);
      this.toast.success('Новая версия создана и активирована.');
      this.editorNote.set('');
      await this.selectPrompt(key);
      await this.loadPrompts();
    } catch {
      this.toast.error('Не удалось сохранить версию.');
    } finally {
      this.editorBusy.set(false);
    }
  }

  async activateVersion(version: PromptVersion): Promise<void> {
    const key = this.selectedPromptKey();
    if (!key || version.isActive) return;

    const ok = await this.confirm.confirm({
      title: `Откатить на версию ${version.version}?`,
      message: 'Конвейер сразу начнёт использовать этот текст промпта для новых задач.',
      confirmText: 'Активировать',
    });
    if (!ok) return;

    this.editorBusy.set(true);
    try {
      await this.api.activatePromptVersion(key, version.version);
      this.toast.success(`Версия ${version.version} активирована.`);
      await this.selectPrompt(key);
      await this.loadPrompts();
    } catch {
      this.toast.error('Не удалось активировать версию.');
    } finally {
      this.editorBusy.set(false);
    }
  }

  async runDryRun(): Promise<void> {
    const key = this.selectedPromptKey();
    if (!key || !this.dryRunUserText().trim()) return;

    this.dryRunBusy.set(true);
    this.dryRunResult.set(null);
    try {
      this.dryRunResult.set(await this.api.dryRunPrompt(key, this.editorBody(), this.dryRunUserText()));
    } catch {
      this.toast.error('Не удалось прогнать промпт.');
    } finally {
      this.dryRunBusy.set(false);
    }
  }

  // --- Задачи ---

  async loadJobs(): Promise<void> {
    this.jobsLoading.set(true);
    try {
      const page = await this.api.getPipelineJobs(this.jobType(), this.jobStatus() || null, 0, 25, this.jobReason());
      this.jobs.set(page.rows);
      this.jobsTotal.set(page.total);
      this.selectedJobIds.set(new Set());
      this.scheduleJobsPollIfRunning();
    } catch {
      this.toast.error('Не удалось загрузить список задач.');
    } finally {
      this.jobsLoading.set(false);
    }
  }

  /** Автообновление (§8 плана) — пока в выдаче есть Pending/Running, список сам подтягивает
   * актуальные статусы; тот же приём, что AdminEnrichmentComponent.scheduleRebuildPollIfRunning. */
  private scheduleJobsPollIfRunning(): void {
    clearTimeout(this.jobPollTimer);
    if (!this.jobs().some((j) => j.status === 'Pending' || j.status === 'Running')) return;

    this.jobPollTimer = setTimeout(async () => {
      try {
        const page = await this.api.getPipelineJobs(this.jobType(), this.jobStatus() || null, 0, 25, this.jobReason());
        this.jobs.set(page.rows);
        this.jobsTotal.set(page.total);
      } catch {
        // Транзиентная ошибка поллинга — пробуем снова на следующем тике.
      }
      this.scheduleJobsPollIfRunning();
    }, JOB_POLL_INTERVAL_MS);
  }

  async selectJobType(type: PipelineJobType): Promise<void> {
    this.jobType.set(type);
    this.updateQueryParams({ type });
    await this.loadJobs();
  }

  async selectJobStatus(status: string): Promise<void> {
    this.jobStatus.set(status);
    this.updateQueryParams({ status: status || null });
    await this.loadJobs();
  }

  clearJobReason(): void {
    this.jobReason.set(null);
    this.updateQueryParams({ reason: null });
    void this.loadJobs();
  }

  openJob(job: PipelineJob): void {
    this.openJobId.set(job.id);
    this.updateQueryParams({ job: job.id });
  }

  closeJobPanel(): void {
    this.openJobId.set(null);
    this.updateQueryParams({ job: null });
  }

  async retryJob(job: PipelineJob): Promise<void> {
    this.jobsBusy.set(true);
    try {
      await this.api.retryPipelineJob(job.id, job.type);
      this.toast.success('Задача поставлена в очередь заново.');
      await this.loadJobs();
    } catch {
      this.toast.error('Не удалось перезапустить задачу.');
    } finally {
      this.jobsBusy.set(false);
    }
  }

  toggleJobSelection(id: string): void {
    this.selectedJobIds.update((set) => {
      const copy = new Set(set);
      if (copy.has(id)) copy.delete(id);
      else copy.add(id);
      return copy;
    });
  }

  async bulkRetrySelected(): Promise<void> {
    const ids = [...this.selectedJobIds()];
    if (ids.length === 0) return;

    this.jobsBusy.set(true);
    try {
      const result = await this.api.bulkRetryJobs(this.jobType(), ids);
      this.toast.success(`Перезапущено задач: ${result.retriedCount}.`);
      await this.loadJobs();
    } catch {
      this.toast.error('Не удалось перезапустить выбранные задачи.');
    } finally {
      this.jobsBusy.set(false);
    }
  }

  async deleteJob(job: PipelineJob): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Удалить задачу?',
      message: `«${job.displayName}» будет удалена насовсем — это не перезапуск, а очистка списка.`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;

    this.jobsBusy.set(true);
    try {
      await this.api.deleteJob(job.id, job.type);
      if (this.openJobId() === job.id) this.closeJobPanel();
      await this.loadJobs();
    } catch {
      this.toast.error('Не удалось удалить задачу.');
    } finally {
      this.jobsBusy.set(false);
    }
  }

  async bulkDeleteSelected(): Promise<void> {
    const ids = [...this.selectedJobIds()];
    if (ids.length === 0) return;

    const ok = await this.confirm.confirm({
      title: `Удалить ${ids.length} задач?`,
      message: 'Насовсем — это не перезапуск, а очистка списка.',
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;

    this.jobsBusy.set(true);
    try {
      const result = await this.api.bulkDeleteJobs(this.jobType(), ids);
      this.toast.success(`Удалено задач: ${result.deletedCount}.`);
      await this.loadJobs();
    } catch {
      this.toast.error('Не удалось удалить выбранные задачи.');
    } finally {
      this.jobsBusy.set(false);
    }
  }

  // --- LM Studio ---

  async loadLmStudioModel(): Promise<void> {
    this.lmStudioLoading.set(true);
    try {
      const info = await this.api.getLmStudioModel();
      this.lmStudioActiveModel.set(info.activeModel);
      this.lmStudioFallbackModel.set(info.fallbackModel);
      this.lmStudioSelectedModel.set(info.activeModel ?? info.fallbackModel);
      this.lmStudioAvailable.set(await this.api.getAvailableLmStudioModels());
    } catch {
      this.toast.error('Не удалось загрузить настройки LM Studio.');
    } finally {
      this.lmStudioLoading.set(false);
    }
  }

  async saveLmStudioModel(): Promise<void> {
    this.lmStudioBusy.set(true);
    try {
      await this.api.setLmStudioModel(this.lmStudioSelectedModel().trim() || null);
      this.toast.success('Модель обновлена.');
      await this.loadLmStudioModel();
    } catch {
      this.toast.error('Не удалось сохранить модель.');
    } finally {
      this.lmStudioBusy.set(false);
    }
  }

  async resetLmStudioModel(): Promise<void> {
    this.lmStudioBusy.set(true);
    try {
      await this.api.setLmStudioModel(null);
      this.toast.success('Возврат к модели по умолчанию.');
      await this.loadLmStudioModel();
    } catch {
      this.toast.error('Не удалось сбросить модель.');
    } finally {
      this.lmStudioBusy.set(false);
    }
  }

  async loadLmStudioReasoning(): Promise<void> {
    this.lmStudioReasoningLoading.set(true);
    try {
      const info = await this.api.getLmStudioReasoning();
      this.lmStudioReasoningActive.set(info.activeReasoning);
      this.lmStudioReasoningFallback.set(info.fallbackReasoning);
      this.lmStudioReasoningSelected.set(info.activeReasoning ?? info.fallbackReasoning);
    } catch {
      this.toast.error('Не удалось загрузить уровень рассуждений.');
    } finally {
      this.lmStudioReasoningLoading.set(false);
    }
  }

  async saveLmStudioReasoning(): Promise<void> {
    this.lmStudioReasoningBusy.set(true);
    try {
      await this.api.setLmStudioReasoning(this.lmStudioReasoningSelected());
      this.toast.success('Уровень рассуждений обновлён.');
      await this.loadLmStudioReasoning();
    } catch {
      this.toast.error('Не удалось сохранить уровень рассуждений.');
    } finally {
      this.lmStudioReasoningBusy.set(false);
    }
  }

  async resetLmStudioReasoning(): Promise<void> {
    this.lmStudioReasoningBusy.set(true);
    try {
      await this.api.setLmStudioReasoning(null);
      this.toast.success('Возврат к уровню рассуждений по умолчанию.');
      await this.loadLmStudioReasoning();
    } catch {
      this.toast.error('Не удалось сбросить уровень рассуждений.');
    } finally {
      this.lmStudioReasoningBusy.set(false);
    }
  }
}
