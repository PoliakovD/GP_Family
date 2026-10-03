import { Component, OnDestroy, OnInit, inject, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MedicalRecordKind } from '../../models/types';
import type { GlobalSpecimenDto, MedicalRecord, UpdateMedicalRecordRequest, UserSpecimen } from '../../models/types';
import { ApiError, ApiService } from '../../services/api.service';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { ToastService } from '../../shared/toast/toast.service';
import type { MedicalRecordKindLabels } from '../../shared/util/medical-record-labels';

/**
 * Шторка «Редактировать запись» (вынесена из MedicalRecordsPanelComponent): название, дата, врач,
 * описание и — у анализов — биоматериал ВСЕЙ записи (смена каскадится на все показатели и
 * помечает резюме устаревшим, пересчёт идёт в фоне).
 *
 * Биоматериал — свободный текст с подсказками из общего справочника; при уходе фокуса текст
 * резолвится find-or-register (POST /api/specimens с LLM-проверкой). Если ИИ недоступен (503),
 * название сохраняется как «ожидает проверки» и применяется бэкендом позже.
 */
@Component({
  selector: 'app-record-edit-sheet',
  standalone: true,
  imports: [FormsModule, BottomSheetComponent],
  templateUrl: './record-edit-sheet.component.html',
})
export class RecordEditSheetComponent implements OnInit, OnDestroy {
  readonly record = input.required<MedicalRecord>();
  /** Подсказка модели («мазок» без локализации) — предзаполняет поле из баннера «уточните». */
  readonly specimenHint = input<string | null | undefined>(null);
  readonly labels = input.required<MedicalRecordKindLabels>();
  /** id <datalist> с подсказками врачей — он живёт у родителя (общий с фильтром). */
  readonly doctorsDatalistId = input.required<string>();

  readonly closed = output<void>();
  /** Сохранено. Значение — информационное сообщение для плашки родителя (или null). */
  readonly saved = output<string | null>();

  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

  protected readonly Kind = MedicalRecordKind;

  form: UpdateMedicalRecordRequest = { recordDate: '', doctor: '', description: '', title: '' };
  saving = false;

  specimenQuery = '';
  private specimenForm: { specimenKbId: string } = { specimenKbId: '' };
  specimenSuggestions: GlobalSpecimenDto[] = [];
  customSpecimens: UserSpecimen[] = [];
  specimenError: string | null = null;
  checkingSpecimen = false;
  /** Проверка биоматериала не состоялась из-за недоступного ИИ — при сохранении название уйдёт в
   * «ожидает проверки» (PUT .../specimen-pending), а не потеряется. */
  specimenCheckDeferred = false;
  private specimenSearchTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnInit(): void {
    const record = this.record();
    this.specimenQuery = this.specimenHint() ?? record.specimenDisplayName ?? '';
    this.specimenForm = { specimenKbId: record.specimenKbId };
    this.form = {
      recordDate: record.recordDate,
      doctor: record.doctor ?? '',
      description: record.description ?? '',
      title: record.title ?? '',
    };
    if (record.kind === MedicalRecordKind.Analysis) {
      void this.api.getSpecimens().then((s) => (this.customSpecimens = s)).catch(() => undefined);
    }
  }

  ngOnDestroy(): void {
    if (this.specimenSearchTimer) clearTimeout(this.specimenSearchTimer);
  }

  /** Плейсхолдер поля «Название» — как shortName() у панели. */
  protected shortName(): string {
    const record = this.record();
    return record.title ?? (record.kind === MedicalRecordKind.Analysis ? 'Анализ' : 'Приём врача');
  }

  /** Debounce поиска подсказок (GET /api/specimens/search). */
  protected onSpecimenQueryInput(q: string): void {
    if (this.specimenSearchTimer) clearTimeout(this.specimenSearchTimer);
    this.specimenSearchTimer = setTimeout(() => void this.searchSpecimens(q), 200);
  }

  private async searchSpecimens(q: string): Promise<void> {
    try {
      this.specimenSuggestions = await this.api.searchSpecimens(q);
    } catch {
      // Подсказка необязательна для работы формы — при сбое сети оставляем прежний список.
    }
  }

  /** Резолвит введённый текст в ссылку на справочник: совпадение среди подсказок — без сети,
   * новый текст — find-or-register. */
  protected async resolveSpecimen(): Promise<void> {
    const trimmed = this.specimenQuery.trim();
    if (!trimmed || this.checkingSpecimen) return;

    const existing = this.specimenSuggestions.find((s) => s.displayName.toLowerCase() === trimmed.toLowerCase());
    if (existing) {
      this.specimenForm.specimenKbId = existing.id;
      this.specimenError = null;
      return;
    }

    this.checkingSpecimen = true;
    this.specimenError = null;
    this.specimenCheckDeferred = false;
    try {
      const created = await this.api.createSpecimen(trimmed);
      this.specimenForm.specimenKbId = created.specimenKbId;
      if (!this.customSpecimens.some((s) => s.specimenKbId === created.specimenKbId)) {
        this.customSpecimens = [...this.customSpecimens, created].sort((a, b) => a.displayName.localeCompare(b.displayName, 'ru'));
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 503) {
        this.specimenCheckDeferred = true; // ИИ недоступен — не ошибка ввода
      } else {
        this.specimenError = err instanceof ApiError ? err.message : 'Не удалось проверить источник показателя.';
      }
    } finally {
      this.checkingSpecimen = false;
    }
  }

  protected async save(): Promise<void> {
    if (!this.form.recordDate || this.saving || this.checkingSpecimen) return;
    const record = this.record();
    this.saving = true;
    try {
      // Биоматериал — только у анализов и только если его реально сменили: новый текст мог ещё не
      // пройти find-or-register (уход фокуса не дождался), добираем здесь.
      const query = this.specimenQuery.trim();
      const specimenChanged = record.kind === MedicalRecordKind.Analysis
        && query !== '' && query !== (record.specimenDisplayName ?? '');
      if (specimenChanged && this.specimenForm.specimenKbId === record.specimenKbId) {
        await this.resolveSpecimen();
      }
      if (this.specimenError) return;

      await this.api.updateMedicalRecord(record.id, {
        recordDate: this.form.recordDate,
        doctor: this.form.doctor?.trim() || null,
        description: this.form.description?.trim() || null,
        title: this.form.title?.trim() || null,
      });
      let info: string | null = null;
      const newSpecimenId = this.specimenForm.specimenKbId;
      if (specimenChanged && newSpecimenId && newSpecimenId !== record.specimenKbId) {
        await this.api.setRecordSpecimen(record.id, newSpecimenId);
      } else if (specimenChanged && this.specimenCheckDeferred) {
        await this.api.setPendingSpecimen(record.id, query);
        info = `ИИ сейчас недоступен — биоматериал «${query}» будет проверен и применён к записи автоматически, когда он вернётся.`;
      }
      this.saved.emit(info);
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось сохранить изменения.');
    } finally {
      this.saving = false;
    }
  }
}
