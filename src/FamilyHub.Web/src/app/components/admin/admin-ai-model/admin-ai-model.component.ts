import { Component, HostListener, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AdminApiService,
  LmStudioAvailableModels,
  LmStudioReasoning,
} from '../../../services/admin-api.service';
import { HasUnsavedChanges } from '../../../services/unsaved-changes.guard';
import { ToastService } from '../../../shared/toast/toast.service';

const REASONING_OPTIONS: { value: LmStudioReasoning; label: string }[] = [
  { value: LmStudioReasoning.None, label: 'Без рассуждений (быстрее)' },
  { value: LmStudioReasoning.Minimal, label: 'Минимальные' },
  { value: LmStudioReasoning.Medium, label: 'Умеренные' },
  { value: LmStudioReasoning.Maximum, label: 'Максимальные (медленнее)' },
];

/**
 * «Настройки → ИИ-модель»: модель LM Studio и уровень рассуждений (раньше — четвёртая вкладка
 * «Пайплайн → LM Studio»). Обе настройки применяются сразу, без передеплоя, поэтому сохраняются
 * явной кнопкой «Сохранить» на своей карточке: выбор из списка — не переключатель, случайный клик
 * не должен молча менять модель для всего конвейера. Пока карточка изменена, а не сохранена,
 * уход со страницы переспрашивается (unsavedChangesGuard + beforeunload).
 *
 * Значения «по умолчанию» приходят из env (LmStudio__Model / LmStudio__Reasoning), а не из
 * appsettings — поэтому и подпись «по умолчанию (env)».
 */
@Component({
    selector: 'app-admin-ai-model',
    imports: [FormsModule],
    templateUrl: './admin-ai-model.component.html'
})
export class AdminAiModelComponent implements OnInit, HasUnsavedChanges {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);

  readonly reasoningOptions = REASONING_OPTIONS;

  // --- Модель ---
  readonly modelActive = signal<string | null>(null);
  readonly modelFallback = signal('');
  readonly modelAvailable = signal<LmStudioAvailableModels | null>(null);
  readonly modelSelected = signal('');
  readonly modelLoading = signal(true);
  readonly modelBusy = signal(false);
  readonly modelError = signal<string | null>(null);

  // Уровень рассуждений — отдельные сигналы: независимая загрузка/сохранение, ошибка одной
  // карточки не должна блокировать кнопку другой.
  readonly reasoningActive = signal<LmStudioReasoning | null>(null);
  readonly reasoningFallback = signal<LmStudioReasoning | null>(null);
  readonly reasoningSelected = signal<LmStudioReasoning>(LmStudioReasoning.None);
  readonly reasoningLoading = signal(true);
  readonly reasoningBusy = signal(false);
  readonly reasoningError = signal<string | null>(null);

  /** Модель, которая действует прямо сейчас (выбранная в БД либо значение из env). */
  readonly modelEffective = computed(() => this.modelActive() ?? this.modelFallback());
  readonly reasoningEffective = computed(() => this.reasoningActive() ?? this.reasoningFallback());

  readonly modelDirty = computed(
    () => !this.modelLoading() && this.modelSelected().trim() !== this.modelEffective(),
  );
  readonly reasoningDirty = computed(
    () => !this.reasoningLoading() && this.reasoningSelected() !== this.reasoningEffective(),
  );

  ngOnInit(): void {
    void this.loadModel();
    void this.loadReasoning();
  }

  hasUnsavedChanges(): boolean {
    return this.modelDirty() || this.reasoningDirty();
  }

  /** Закрытие вкладки браузера CanDeactivate не видит — просим нативное подтверждение. */
  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.hasUnsavedChanges()) event.preventDefault();
  }

  reasoningLabel(value: LmStudioReasoning | null): string {
    if (value === null) return '—';
    return REASONING_OPTIONS.find((o) => o.value === value)?.label ?? String(value);
  }

  // --- Модель ---

  async loadModel(): Promise<void> {
    this.modelLoading.set(true);
    this.modelError.set(null);
    try {
      const info = await this.api.getLmStudioModel();
      this.modelActive.set(info.activeModel);
      this.modelFallback.set(info.fallbackModel);
      this.modelSelected.set(info.activeModel ?? info.fallbackModel);
      this.modelAvailable.set(await this.api.getAvailableLmStudioModels());
    } catch {
      this.modelError.set('Не удалось загрузить настройки модели LM Studio.');
    } finally {
      this.modelLoading.set(false);
    }
  }

  async saveModel(): Promise<void> {
    this.modelBusy.set(true);
    try {
      await this.api.setLmStudioModel(this.modelSelected().trim() || null);
      this.toast.success('Модель обновлена.');
      await this.loadModel();
    } catch {
      this.toast.error('Не удалось сохранить модель.');
    } finally {
      this.modelBusy.set(false);
    }
  }

  async resetModel(): Promise<void> {
    this.modelBusy.set(true);
    try {
      await this.api.setLmStudioModel(null);
      this.toast.success('Возврат к модели по умолчанию.');
      await this.loadModel();
    } catch {
      this.toast.error('Не удалось сбросить модель.');
    } finally {
      this.modelBusy.set(false);
    }
  }

  // --- Уровень рассуждений ---

  async loadReasoning(): Promise<void> {
    this.reasoningLoading.set(true);
    this.reasoningError.set(null);
    try {
      const info = await this.api.getLmStudioReasoning();
      this.reasoningActive.set(info.activeReasoning);
      this.reasoningFallback.set(info.fallbackReasoning);
      this.reasoningSelected.set(info.activeReasoning ?? info.fallbackReasoning);
    } catch {
      this.reasoningError.set('Не удалось загрузить уровень рассуждений.');
    } finally {
      this.reasoningLoading.set(false);
    }
  }

  async saveReasoning(): Promise<void> {
    this.reasoningBusy.set(true);
    try {
      await this.api.setLmStudioReasoning(this.reasoningSelected());
      this.toast.success('Уровень рассуждений обновлён.');
      await this.loadReasoning();
    } catch {
      this.toast.error('Не удалось сохранить уровень рассуждений.');
    } finally {
      this.reasoningBusy.set(false);
    }
  }

  async resetReasoning(): Promise<void> {
    this.reasoningBusy.set(true);
    try {
      await this.api.setLmStudioReasoning(null);
      this.toast.success('Возврат к уровню рассуждений по умолчанию.');
      await this.loadReasoning();
    } catch {
      this.toast.error('Не удалось сбросить уровень рассуждений.');
    } finally {
      this.reasoningBusy.set(false);
    }
  }
}
