import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe, JsonPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  AdminApiService,
  PipelineJob,
  PipelineJobType,
  PipelineStep,
  PromptSlot,
  PromptVersion,
} from '../../../services/admin-api.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';

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
  imports: [FormsModule, DatePipe, JsonPipe],
  templateUrl: './admin-pipeline.component.html',
})
export class AdminPipelineComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly jobTypes = JOB_TYPES;

  readonly tab = signal<'steps' | 'prompts' | 'jobs'>('steps');

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
  readonly jobs = signal<PipelineJob[]>([]);
  readonly jobsTotal = signal(0);
  readonly jobsLoading = signal(false);
  readonly jobsBusy = signal(false);

  ngOnInit(): void {
    void this.loadSteps();
  }

  selectTab(tab: 'steps' | 'prompts' | 'jobs'): void {
    this.tab.set(tab);
    if (tab === 'prompts' && this.promptSlots().length === 0) void this.loadPrompts();
    if (tab === 'jobs' && this.jobs().length === 0) void this.loadJobs();
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
      const page = await this.api.getPipelineJobs(this.jobType(), this.jobStatus() || null, 0, 25);
      this.jobs.set(page.rows);
      this.jobsTotal.set(page.total);
    } catch {
      this.toast.error('Не удалось загрузить список задач.');
    } finally {
      this.jobsLoading.set(false);
    }
  }

  async selectJobType(type: PipelineJobType): Promise<void> {
    this.jobType.set(type);
    await this.loadJobs();
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
}
