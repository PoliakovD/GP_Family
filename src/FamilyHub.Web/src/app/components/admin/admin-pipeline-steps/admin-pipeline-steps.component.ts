import { Component, OnInit, inject, signal } from '@angular/core';
import { AdminApiService, PipelineStep } from '../../../services/admin-api.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { ToastService } from '../../../shared/toast/toast.service';

/**
 * «Настройки → Шаги пайплайна»: вкл/выкл необязательных шагов четырёх конвейеров (§2 плана).
 * Порядок вызовов зашит в коде — реальная последовательность определяется жёсткими зависимостями
 * между шагами одного прогона (см. class doc AdminPipelineEndpoints на бэкенде), из админки
 * доступно только вкл/выкл.
 *
 * Переключатель сохраняется сразу, с тостом «Отменить». Выключение дополнительно переспрашивается:
 * шаг перестаёт выполняться для всех новых задач, и последствия (пропущенная коррекция OCR,
 * пустая суммаризация) проявятся не сразу. Включение безопасно — подтверждения не требует.
 */
@Component({
  selector: 'app-admin-pipeline-steps',
  standalone: true,
  templateUrl: './admin-pipeline-steps.component.html',
})
export class AdminPipelineStepsComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly steps = signal<PipelineStep[]>([]);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.steps.set(await this.api.getPipelineSteps());
    } catch {
      this.error.set('Не удалось загрузить шаги пайплайна.');
    } finally {
      this.loading.set(false);
    }
  }

  async toggle(step: PipelineStep, event: Event): Promise<void> {
    if (step.isMandatory) return;

    // Нативный чекбокс уже сменил своё состояние — при отмене/ошибке возвращаем его вручную:
    // привязка [checked] не перерисует его, пока значение в state не поменялось.
    const input = event.target as HTMLInputElement;
    const revert = () => (input.checked = step.isEnabled);

    const enable = !step.isEnabled;
    if (!enable) {
      const ok = await this.confirm.confirm({
        title: 'Выключить шаг?',
        message: `Шаг «${step.stepKey}» пайплайна «${step.pipelineKey}» перестанет выполняться для всех новых задач. Уже выполненное не изменится.`,
        confirmText: 'Выключить',
        danger: true,
      });
      if (!ok) {
        revert();
        return;
      }
    }

    this.busy.set(true);
    try {
      await this.api.setStepEnabled(step.pipelineKey, step.stepKey, enable);
      await this.load();
      this.toast.successWithUndo(
        enable ? `Шаг «${step.stepKey}» включён.` : `Шаг «${step.stepKey}» выключен.`,
        () => this.undo(step, !enable),
      );
    } catch {
      revert();
      this.toast.error('Не удалось изменить шаг.');
    } finally {
      this.busy.set(false);
    }
  }

  private async undo(step: PipelineStep, enabled: boolean): Promise<void> {
    try {
      await this.api.setStepEnabled(step.pipelineKey, step.stepKey, enabled);
      await this.load();
      this.toast.info('Изменение отменено.');
    } catch {
      this.toast.error('Не удалось отменить изменение.');
    }
  }
}
