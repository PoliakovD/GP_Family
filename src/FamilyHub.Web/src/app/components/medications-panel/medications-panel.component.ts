import { Component, EventEmitter, OnInit, Output, effect, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import type { Medication } from '../../models/types';
import { ToastService } from '../../shared/toast/toast.service';
import { compressImage } from '../../shared/util/image-compression';
import { expiryClass } from '../../shared/util/expiry';
import { matchesQuery } from '../../shared/util/local-filter';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';

const MAX_PHOTOS = 5;
const KNOWN_KEYS = ['instructions', 'quantity'];

interface DataRow {
  key: string;
  value: string;
}

export type RecognizeStep = 'idle' | 'compressing' | 'uploading' | 'recognizing' | 'receiving';

const RECOGNIZE_STEP_ORDER: RecognizeStep[] = ['compressing', 'uploading', 'recognizing', 'receiving'];

let nextInstanceId = 0;

@Component({
  selector: 'app-medications-panel',
  standalone: true,
  imports: [FormsModule, LoadingSpinnerComponent, SearchFieldComponent],
  templateUrl: './medications-panel.component.html',
  styleUrl: './medications-panel.component.scss',
})
export class MedicationsPanelComponent implements OnInit {
  readonly medkitId = input.required<string>();

  /** Сообщает родителю (карточке аптечки) актуальное число медикаментов — она показывает
   * его в свёрнутом виде и иначе не узнала бы об изменениях, сделанных внутри этой панели. */
  @Output() readonly countChanged = new EventEmitter<number>();

  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

  activeTab: 'list' | 'add' = 'list';

  /** Уникален на инстанс — несколько панелей (по одной на аптечку) могут быть смонтированы одновременно (аккордеон),
   * а <label for> должен указывать ровно на "свой" скрытый file input. */
  readonly photoInputId = `medication-photo-input-${nextInstanceId++}`;

  items: Medication[] = [];
  /** Локальный фильтр внутри уже загруженной аптечки — не источник в SearchService, заводить
   * серверный поиск ради одной аптечки на сотню наименований не нужно (см. plan). */
  searchQuery = '';
  form = { name: '', expiryDate: '', instructions: '', quantity: '' };
  extraRows: DataRow[] = [];
  editingId: string | null = null;
  error: string | null = null;
  loading = true;

  // Фото — только для распознавания, никуда не сохраняются и не загружаются как вложения.
  photos: { file: File; previewUrl: string }[] = [];
  recognizeStep: RecognizeStep = 'idle';
  uploadProgress = 0;

  readonly recognizeSteps: { id: RecognizeStep; label: string }[] = [
    { id: 'compressing', label: 'Сжимаем' },
    { id: 'uploading', label: 'Отправляем' },
    { id: 'recognizing', label: 'Распознаём' },
    { id: 'receiving', label: 'Получаем' },
  ];

  get recognizing(): boolean {
    return this.recognizeStep !== 'idle';
  }

  /** Фильтр по названию, инструкции и всем доп. полям (в т.ч. найденным при OCR-распознавании). */
  get filteredItems(): Medication[] {
    return this.items.filter((item) =>
      matchesQuery(this.searchQuery, item.name, ...Object.values(item.data ?? {})));
  }

  stepState(id: RecognizeStep): 'done' | 'active' | 'pending' {
    const activeIndex = RECOGNIZE_STEP_ORDER.indexOf(this.recognizeStep);
    if (activeIndex < 0) return 'pending';

    const idIndex = RECOGNIZE_STEP_ORDER.indexOf(id);
    if (idIndex < activeIndex) return 'done';
    if (idIndex === activeIndex) return 'active';
    return 'pending';
  }

  // undefined — ещё ни разу не загружали.
  private loadedMedkitId: string | undefined = undefined;

  constructor() {
    // Реагирует на смену аптечки (например, при раскрытии другой карточки), пока панель открыта.
    // Первичная загрузка при монтировании — в ngOnInit (effect() выполняется только на
    // следующем цикле change detection и может не успеть отработать).
    effect(() => {
      const id = this.medkitId();
      if (id === this.loadedMedkitId) return;
      this.resetForm();
      this.activeTab = 'list';
      this.searchQuery = '';
      void this.refresh();
    });
  }

  ngOnInit(): void {
    if (this.medkitId() !== this.loadedMedkitId) {
      void this.refresh();
    }
  }

  async refresh(): Promise<void> {
    const id = this.medkitId();
    this.loadedMedkitId = id;
    this.loading = true;
    try {
      this.items = await this.api.getMedications(id);
      this.error = null;
      this.countChanged.emit(this.items.length);
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить аптечку.';
    } finally {
      this.loading = false;
    }
  }

  /** Цветовая индикация по сроку годности — общая с плоским списком поиска Аптечки (MedicationsTabComponent). */
  expiryClassFor(item: Medication): string {
    return expiryClass(item.expiryDate);
  }

  dataEntries(item: Medication): DataRow[] {
    return Object.entries(item.data ?? {})
      .filter(([key]) => !KNOWN_KEYS.includes(key))
      .map(([key, value]) => ({ key, value }));
  }

  instructionsOf(item: Medication): string | null {
    return item.data?.['instructions'] || null;
  }

  quantityOf(item: Medication): string | null {
    return item.data?.['quantity'] || null;
  }

  // --- Фото и распознавание ---

  onPhotosSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = ''; // позволяет выбрать те же файлы повторно

    if (files.length === 0) return;

    const room = MAX_PHOTOS - this.photos.length;
    if (room <= 0) {
      this.toast.error(`Можно прикрепить не более ${MAX_PHOTOS} фото.`);
      return;
    }

    const accepted = files.slice(0, room);
    if (files.length > accepted.length) {
      this.toast.error(`Можно прикрепить не более ${MAX_PHOTOS} фото — лишние не добавлены.`);
    }

    for (const file of accepted) {
      this.photos.push({ file, previewUrl: URL.createObjectURL(file) });
    }
  }

  removePhoto(index: number): void {
    const [removed] = this.photos.splice(index, 1);
    if (removed) URL.revokeObjectURL(removed.previewUrl);
  }

  async handleRecognize(): Promise<void> {
    if (this.photos.length === 0) return;

    this.recognizeStep = 'compressing';
    this.uploadProgress = 0;
    try {
      const compressed = await Promise.all(this.photos.map((p) => compressImage(p.file)));

      this.recognizeStep = 'uploading';
      const response = await this.api.ocrMedicationPhotos(compressed, (percent) => {
        this.uploadProgress = percent;
        // Отправка файлов завершена (100%) — дальше клиент просто ждёт ответ модели.
        if (percent >= 100) this.recognizeStep = 'recognizing';
      });

      this.recognizeStep = 'receiving';
      await sleep(300); // без паузы шаг "Получаем" отрисовался бы на 0мс — весь код ниже синхронный

      if (!response.success) {
        this.toast.error(response.error ?? 'Не удалось распознать препарат по фото — заполните поля вручную.');
        return;
      }

      if (response.name) this.form.name = response.name;
      if (response.expiryDate) {
        const iso = parseDdMmYyyyToIso(response.expiryDate);
        if (iso) this.form.expiryDate = iso;
      }
      if (response.data) this.mergeExtraRows(response.data);

      this.toast.success('Данные распознаны — проверьте перед сохранением.');
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось распознать препарат по фото.');
    } finally {
      this.recognizeStep = 'idle';
    }
  }

  private mergeExtraRows(data: Record<string, string>): void {
    for (const [key, value] of Object.entries(data)) {
      if (!value) continue;
      const existing = this.extraRows.find((r) => r.key === key);
      if (existing) {
        existing.value = value;
      } else {
        this.extraRows.push({ key, value });
      }
    }
  }

  addExtraRow(): void {
    this.extraRows.push({ key: '', value: '' });
  }

  removeExtraRow(index: number): void {
    this.extraRows.splice(index, 1);
  }

  // --- CRUD ---

  async handleSubmit(): Promise<void> {
    if (!this.form.name.trim()) return;

    const data: Record<string, string> = {};
    if (this.form.instructions.trim()) data['instructions'] = this.form.instructions.trim();
    if (this.form.quantity.trim()) data['quantity'] = this.form.quantity.trim();
    for (const row of this.extraRows) {
      if (row.key.trim()) data[row.key.trim()] = row.value.trim();
    }

    const payload = {
      name: this.form.name.trim(),
      expiryDate: this.form.expiryDate || null,
      data,
    };

    try {
      if (this.editingId) {
        await this.api.updateMedication(this.editingId, payload);
      } else {
        await this.api.createMedication(this.medkitId(), payload);
      }
      this.resetForm();
      this.activeTab = 'list';
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось сохранить запись.';
    }
  }

  openAddTab(): void {
    this.resetForm();
    this.activeTab = 'add';
  }

  startEdit(item: Medication): void {
    this.editingId = item.id;
    this.form = {
      name: item.name,
      expiryDate: item.expiryDate ?? '',
      instructions: item.data?.['instructions'] ?? '',
      quantity: item.data?.['quantity'] ?? '1',
    };
    this.extraRows = this.dataEntries(item);
    this.clearPhotos();
    this.activeTab = 'add';
  }

  cancelEdit(): void {
    this.resetForm();
    this.activeTab = 'list';
  }

  async handleDelete(id: string): Promise<void> {
    try {
      await this.api.deleteMedication(id);
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось удалить запись.';
    }
  }

  resetForm(): void {
    this.form = { name: '', expiryDate: '', instructions: '', quantity: '1' };
    this.extraRows = [];
    this.editingId = null;
    this.clearPhotos();
  }

  private clearPhotos(): void {
    this.photos.forEach((p) => URL.revokeObjectURL(p.previewUrl));
    this.photos = [];
  }
}

/** "dd/MM/yyyy" (как просим модель отдавать) -> "yyyy-MM-dd" для <input type="date">. null, если не распарсилось. */
function parseDdMmYyyyToIso(value: string): string | null {
  const match = /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/.exec(value.trim());
  if (!match) return null;
  const [, day, month, year] = match;
  return `${year}-${month.padStart(2, '0')}-${day.padStart(2, '0')}`;
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
