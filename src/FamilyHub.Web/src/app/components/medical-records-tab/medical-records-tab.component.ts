import { Component, OnInit, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import { TelegramService } from '../../services/telegram.service';
import { FamilyStateService } from '../../services/family-state.service';
import type { Attachment, MedicalRecord, SearchResultItem } from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';
import { DebouncedSearch, SEARCH_MIN_QUERY_LENGTH } from '../../shared/util/debounced-search';

@Component({
  selector: 'app-medical-records-tab',
  standalone: true,
  imports: [FormsModule, LoadingSpinnerComponent, BottomSheetComponent, SearchFieldComponent],
  templateUrl: './medical-records-tab.component.html',
})
export class MedicalRecordsTabComponent implements OnInit {
  readonly state = inject(FamilyStateService);
  private readonly api = inject(ApiService);
  private readonly tg = inject(TelegramService);

  items: MedicalRecord[] = [];
  form = { personName: '', recordDate: '', doctor: '', description: '' };
  error: string | null = null;
  loading = true;

  /** Поиск по анализам (types=record) — серверный, но рендерим уже загруженные `items`
   * (шторка «Доступ», вложения и т.п. живут только в них; сервер отдаёт только id + score),
   * отфильтрованные и упорядоченные по совпавшим id. */
  readonly search = new DebouncedSearch<SearchResultItem>(
    (q) => this.api.search(q, 'record').then((r) => r.items),
    (err) => (err instanceof ApiError ? err.message : 'Не удалось выполнить поиск.'),
  );

  onSearchQueryChange(value: string): void {
    this.search.query = value;
    this.search.onQueryChange();
  }

  get displayedItems(): MedicalRecord[] {
    if (this.search.query.trim().length < SEARCH_MIN_QUERY_LENGTH || !this.search.searched) {
      return this.items;
    }
    const scoreById = new Map(this.search.items.map((i) => [i.id, i.score]));
    return this.items
      .filter((r) => scoreById.has(r.id))
      .sort((a, b) => scoreById.get(b.id)! - scoreById.get(a.id)!);
  }

  // Бэкенд не отдаёт список вложений отдельным эндпоинтом — храним то, что
  // загрузили в текущей сессии (ответ POST .../attachments содержит Attachment целиком).
  attachmentsByRecord: Record<string, Attachment[]> = {};

  // L1: семьи, которым владелец глобально расшарил свои анализы (см. MedicalRecordService).
  shares: string[] = [];

  // Запись, для которой сейчас открыта шторка «Доступ» (null — шторка закрыта).
  accessRecord: MedicalRecord | null = null;

  ngOnInit(): void {
    this.refresh();
  }

  async refresh(): Promise<void> {
    this.loading = true;
    try {
      const [items, shares] = await Promise.all([
        this.api.getMedicalRecords(),
        this.api.getMedicalRecordShares(),
      ]);
      this.items = items;
      this.shares = shares;
      // Открытая шторка должна остаться синхронной с перезагруженным состоянием записи.
      if (this.accessRecord) {
        this.accessRecord = this.items.find((r) => r.id === this.accessRecord!.id) ?? null;
      }
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить анализы.';
    } finally {
      this.loading = false;
    }
  }

  async handleSubmit(): Promise<void> {
    if (!this.form.personName.trim() || !this.form.recordDate) return;
    try {
      await this.api.createMedicalRecord({
        personName: this.form.personName.trim(),
        recordDate: this.form.recordDate,
        doctor: this.form.doctor.trim() || null,
        description: this.form.description.trim() || null,
        hideFromFamilyIds: null,
      });
      this.form = { personName: '', recordDate: '', doctor: '', description: '' };
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось сохранить запись.';
    }
  }

  async handleUpload(recordId: string, event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    try {
      const attachment = await this.api.uploadAttachment(recordId, file);
      this.attachmentsByRecord = {
        ...this.attachmentsByRecord,
        [recordId]: [...(this.attachmentsByRecord[recordId] ?? []), attachment],
      };
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить файл.';
    }
  }

  async handleOpenAttachment(attachmentId: string): Promise<void> {
    try {
      const { url } = await this.api.getAttachmentUrl(attachmentId);
      this.tg.openExternalLink(url);
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось получить ссылку на файл.';
    }
  }

  attachmentsFor(recordId: string): Attachment[] {
    return this.attachmentsByRecord[recordId] ?? [];
  }

  // --- Доступ (bottom-sheet «Доступ») ---

  openAccessSheet(record: MedicalRecord): void {
    this.accessRecord = record;
  }

  closeAccessSheet(): void {
    this.accessRecord = null;
  }

  /** Видна ли КОНКРЕТНАЯ запись данной семье: (L1 share есть) И (L2 hide нет). */
  isVisibleToFamily(record: MedicalRecord, familyId: string): boolean {
    return this.shares.includes(familyId) && !record.hiddenFamilyIds.includes(familyId);
  }

  /** Видна ли запись хотя бы одной расшаренной семье — определяет активную опцию сегмента. */
  private visibleToAny(record: MedicalRecord): boolean {
    return this.shares.some((fid) => !record.hiddenFamilyIds.includes(fid));
  }

  isOnlyMe(record: MedicalRecord): boolean {
    return this.shares.length === 0 || !this.visibleToAny(record);
  }

  /** Сводка для карточки: «Только вы» / «Все семьи» / «Все семьи, кроме N». */
  accessSummary(record: MedicalRecord): string {
    const total = this.shares.length;
    if (total === 0) return 'Только вы';
    const hiddenCount = this.shares.filter((fid) => record.hiddenFamilyIds.includes(fid)).length;
    if (hiddenCount === total) return 'Только вы';
    if (hiddenCount === 0) return 'Все семьи';
    return `Все семьи, кроме ${hiddenCount}`;
  }

  /**
   * Тумблер одной семьи в шторке. Включение автоматически создаёт L1-шаринг, если его ещё не
   * было — иначе тумблер не мог бы включить видимость семье, которой владелец никогда явно не
   * открывал анализы. Это осознанное поведение: оно делает шторку самодостаточной ценой того,
   * что включение затрагивает базовую видимость всех остальных записей той же семье.
   */
  async setFamilyAccess(record: MedicalRecord, familyId: string, visible: boolean): Promise<void> {
    try {
      if (visible) {
        if (!this.shares.includes(familyId)) {
          await this.api.shareMedicalRecord(familyId);
        }
        await this.api.unhideMedicalRecord(record.id, [familyId]);
      } else {
        await this.api.hideMedicalRecord(record.id, [familyId]);
      }
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Действие доступно только владельцу записи.';
    }
  }

  /** Сегмент «Только я / Все семьи» — bulk-скрытие/раскрытие записи для ВСЕХ уже расшаренных семей. */
  async setAccessMode(record: MedicalRecord, onlyMe: boolean): Promise<void> {
    if (this.shares.length === 0) return;
    try {
      if (onlyMe) {
        await this.api.hideMedicalRecord(record.id, this.shares);
      } else {
        await this.api.unhideMedicalRecord(record.id, this.shares);
      }
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Действие доступно только владельцу записи.';
    }
  }
}
