import { Component, HostListener, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe, JsonPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AdminApiService, PromptSlot, PromptVersion } from '../../../services/admin-api.service';
import { HasUnsavedChanges } from '../../../services/unsaved-changes.guard';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { ToastService } from '../../../shared/toast/toast.service';

/**
 * «Настройки → Промпты»: версии промптов конвейера (создание/откат — ничего не удаляется) и
 * dry-run без записи в справочник (раньше — вкладка «Пайплайн → Промпты»).
 *
 * «Сохранить и активировать» переспрашивается: новая версия сразу становится боевой для всех
 * новых задач — по последствиям это то же самое, что откат на версию, где подтверждение уже было.
 * Пока текст в редакторе отличается от активной версии, а не сохранён, уход со страницы и смена
 * промпта переспрашиваются, чтобы не потерять правки.
 */
@Component({
    selector: 'app-admin-prompts',
    imports: [FormsModule, DatePipe, JsonPipe],
    templateUrl: './admin-prompts.component.html'
})
export class AdminPromptsComponent implements OnInit, HasUnsavedChanges {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly promptSlots = signal<PromptSlot[]>([]);
  readonly promptsLoading = signal(true);
  readonly promptsError = signal<string | null>(null);

  readonly selectedPromptKey = signal<string | null>(null);
  readonly promptVersions = signal<PromptVersion[]>([]);
  readonly promptVersionsLoading = signal(false);
  readonly editorBody = signal('');
  readonly editorNote = signal('');
  readonly editorBusy = signal(false);
  /** Текст активной версии на момент загрузки — точка отсчёта для «есть несохранённые правки». */
  private readonly loadedBody = signal('');

  readonly dryRunUserText = signal('');
  readonly dryRunBusy = signal(false);
  readonly dryRunResult = signal<{ success: boolean; error: string | null; payload: Record<string, unknown> | null } | null>(null);

  readonly dirty = computed(
    () =>
      this.selectedPromptKey() !== null &&
      !this.promptVersionsLoading() &&
      (this.editorBody() !== this.loadedBody() || this.editorNote().trim() !== ''),
  );

  ngOnInit(): void {
    void this.loadPrompts();
  }

  hasUnsavedChanges(): boolean {
    return this.dirty();
  }

  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.dirty()) event.preventDefault();
  }

  async loadPrompts(): Promise<void> {
    this.promptsLoading.set(true);
    this.promptsError.set(null);
    try {
      this.promptSlots.set(await this.api.getPromptSlots());
    } catch {
      this.promptsError.set('Не удалось загрузить список промптов.');
    } finally {
      this.promptsLoading.set(false);
    }
  }

  async selectPrompt(key: string): Promise<void> {
    if (key !== this.selectedPromptKey() && this.dirty()) {
      const ok = await this.confirm.confirm({
        title: 'Есть несохранённые изменения',
        message: 'Если открыть другой промпт, правки в редакторе пропадут.',
        confirmText: 'Открыть другой',
        cancelText: 'Остаться',
        danger: true,
      });
      if (!ok) return;
    }
    await this.loadVersions(key);
  }

  private async loadVersions(key: string): Promise<void> {
    this.selectedPromptKey.set(key);
    this.dryRunResult.set(null);
    this.promptVersionsLoading.set(true);
    try {
      const versions = await this.api.getPromptVersions(key);
      this.promptVersions.set(versions);
      const active = versions.find((v) => v.isActive);
      this.editorBody.set(active?.body ?? '');
      this.loadedBody.set(active?.body ?? '');
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

    const ok = await this.confirm.confirm({
      title: 'Сохранить и активировать новую версию?',
      message: 'Конвейер сразу начнёт использовать этот текст промпта для новых задач. Предыдущая версия останется в истории — к ней можно откатиться.',
      confirmText: 'Сохранить и активировать',
    });
    if (!ok) return;

    this.editorBusy.set(true);
    try {
      await this.api.createPromptVersion(key, this.editorBody(), this.editorNote().trim() || null);
      this.toast.success('Новая версия создана и активирована.');
      await this.loadVersions(key);
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
      message: this.dirty()
        ? 'Конвейер сразу начнёт использовать этот текст промпта для новых задач. Несохранённые правки в редакторе будут потеряны.'
        : 'Конвейер сразу начнёт использовать этот текст промпта для новых задач.',
      confirmText: 'Активировать',
    });
    if (!ok) return;

    this.editorBusy.set(true);
    try {
      await this.api.activatePromptVersion(key, version.version);
      this.toast.success(`Версия ${version.version} активирована.`);
      await this.loadVersions(key);
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
}
