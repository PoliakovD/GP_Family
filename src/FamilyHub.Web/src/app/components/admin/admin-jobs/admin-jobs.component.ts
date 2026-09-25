import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import {
  AdminApiService,
  EnrichmentFailureReasonValue,
  PipelineJob,
  PipelineJobType,
} from '../../../services/admin-api.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { SidePanelComponent } from '../../../shared/side-panel/side-panel.component';
import { ToastService } from '../../../shared/toast/toast.service';
import { AdminJobPanelComponent } from '../admin-job-panel/admin-job-panel.component';
import { JOB_STATUS_OPTIONS } from '../shared/admin-labels';
import { AdminStatusPipe } from '../shared/admin-status.pipe';
import { WebSearchBannerComponent } from '../shared/web-search-banner.component';

const JOB_POLL_INTERVAL_MS = 3000;

const JOB_TYPES: { value: PipelineJobType; label: string }[] = [
  { value: 'lab-analyte', label: 'Обогащение показателей' },
  { value: 'medication', label: 'Обогащение медикаментов' },
  { value: 'visit-medication', label: 'Медикаменты из заключений' },
  { value: 'extraction', label: 'Извлечение из документов' },
];

/**
 * «Операции → Задачи»: листинг задач всех четырёх конвейеров с массовыми действиями и карточкой
 * задачи в боковой панели (раньше — вкладка «Пайплайн → Задачи»).
 *
 * Состояние в URL (§8 плана) — переход из «Требует внимания» (?type=&status=&reason=) и прямые
 * ссылки на конкретную задачу (?job=<guid>) открывают нужный вид без ручных кликов; F5/«назад»
 * сохраняют контекст. Читается один раз при входе — дальнейшая навигация внутри компонента сама
 * пишет параметры обратно (см. updateQueryParams).
 */
@Component({
    selector: 'app-admin-jobs',
    imports: [FormsModule, DatePipe, SidePanelComponent, AdminJobPanelComponent, AdminStatusPipe, WebSearchBannerComponent],
    templateUrl: './admin-jobs.component.html'
})
export class AdminJobsComponent implements OnInit, OnDestroy {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly jobTypes = JOB_TYPES;
  readonly statusOptions = JOB_STATUS_OPTIONS;

  readonly jobType = signal<PipelineJobType>('lab-analyte');
  readonly jobStatus = signal<string>('');
  readonly jobReason = signal<EnrichmentFailureReasonValue | null>(null);
  readonly jobs = signal<PipelineJob[]>([]);
  readonly jobsTotal = signal(0);
  readonly jobsLoading = signal(false);
  readonly jobsBusy = signal(false);
  readonly jobsError = signal<string | null>(null);
  readonly selectedJobIds = signal<Set<string>>(new Set());

  /** Карточка задачи (§7 плана) — открывается из таблицы и из инбокса «Требует внимания» (через
   * query-параметр job). null — панель закрыта. */
  readonly openJobId = signal<string | null>(null);
  private jobPollTimer?: ReturnType<typeof setTimeout>;

  ngOnInit(): void {
    const params = this.route.snapshot.queryParamMap;
    const type = params.get('type') as PipelineJobType | null;
    const status = params.get('status');
    const reason = params.get('reason') as EnrichmentFailureReasonValue | null;
    const job = params.get('job');

    if (type) this.jobType.set(type);
    if (status) this.jobStatus.set(status);
    if (reason) this.jobReason.set(reason);
    if (job) this.openJobId.set(job);

    void this.loadJobs();
  }

  ngOnDestroy(): void {
    clearTimeout(this.jobPollTimer);
  }

  private updateQueryParams(extra: Record<string, string | null>): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: extra,
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  async loadJobs(): Promise<void> {
    this.jobsLoading.set(true);
    this.jobsError.set(null);
    try {
      const page = await this.api.getPipelineJobs(this.jobType(), this.jobStatus() || null, 0, 25, this.jobReason());
      this.jobs.set(page.rows);
      this.jobsTotal.set(page.total);
      this.selectedJobIds.set(new Set());
      this.scheduleJobsPollIfRunning();
    } catch {
      this.jobsError.set('Не удалось загрузить список задач.');
    } finally {
      this.jobsLoading.set(false);
    }
  }

  /** Автообновление (§8 плана) — пока в выдаче есть Pending/Running, список сам подтягивает
   * актуальные статусы. */
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
}
