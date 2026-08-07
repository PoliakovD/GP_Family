import { Component, OnInit, effect, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import { TelegramService } from '../../services/telegram.service';
import { FamilyStateService } from '../../services/family-state.service';
import { AuthService } from '../../services/auth.service';
import { MedicalRecordKind } from '../../models/types';
import type { Attachment, MedicalRecord, SearchResultItem } from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { DebouncedSearch, SEARCH_MIN_QUERY_LENGTH } from '../../shared/util/debounced-search';

interface KindLabels {
  addButtonLabel: string;
  personPlaceholder: string;
  doctorPlaceholder: string;
  descriptionPlaceholder: string;
  searchPlaceholder: string;
  emptyLabel: string;
}

/** Подписи различаются по виду записи — тот же идиом, что TYPE_LABEL/TYPE_ICON в home.component.ts. */
const KIND_LABELS: Record<MedicalRecordKind, KindLabels> = {
  [MedicalRecordKind.Analysis]: {
    addButtonLabel: 'Добавить запись',
    personPlaceholder: 'Пациент',
    doctorPlaceholder: 'Врач (необязательно)',
    descriptionPlaceholder: 'Описание (необязательно)',
    searchPlaceholder: 'Поиск по анализам…',
    emptyLabel: 'Записей нет.',
  },
  [MedicalRecordKind.DoctorVisit]: {
    addButtonLabel: 'Добавить посещение',
    personPlaceholder: 'Пациент',
    doctorPlaceholder: 'Врач / специальность',
    descriptionPlaceholder: 'Заключение (необязательно)',
    searchPlaceholder: 'Поиск по посещениям…',
    emptyLabel: 'Посещений нет.',
  },
};

/** Токен для GET /api/medical-records?kind=. */
const LIST_KIND_TOKEN: Record<MedicalRecordKind, 'analysis' | 'visit'> = {
  [MedicalRecordKind.Analysis]: 'analysis',
  [MedicalRecordKind.DoctorVisit]: 'visit',
};

/** Токен для GET /api/search?types= (SearchDtos.SearchResultType, регистр не важен). */
const SEARCH_TYPE_TOKEN: Record<MedicalRecordKind, string> = {
  [MedicalRecordKind.Analysis]: 'record',
  [MedicalRecordKind.DoctorVisit]: 'visit',
};

/** Одна опция дропдауна "Кто пациент?" — либо «Я» (оба id null), либо подопечный семьи, либо
 * другой активный участник. Составной строковый key нужен для [(ngModel)] на <select>. */
interface PatientOption {
  key: string;
  familyDependentId: string | null;
  targetUserId: string | null;
  label: string;
}

const SELF_OPTION: PatientOption = { key: 'self', familyDependentId: null, targetUserId: null, label: 'Я' };

/**
 * Panel (не Page — своего URL нет, контекст приходит через input): список записей одного вида
 * (анализ/посещение врача — MedicalRecordKind), форма создания, поиск, шторка «Доступ», вложения.
 * Переиспользуется двумя тонкими Page-обёртками: medical-records-tab («Анализы») и
 * doctor-visits-tab («Врачи») — см. .claude/patterns/frontend_web.md про таксономию Page/Panel.
 */
@Component({
  selector: 'app-medical-records-panel',
  standalone: true,
  imports: [FormsModule, LoadingSpinnerComponent, BottomSheetComponent, SearchFieldComponent],
  templateUrl: './medical-records-panel.component.html',
})
export class MedicalRecordsPanelComponent implements OnInit {
  readonly kind = input.required<MedicalRecordKind>();

  readonly state = inject(FamilyStateService);
  private readonly api = inject(ApiService);
  private readonly tg = inject(TelegramService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmService);

  /** Доступен в шаблоне для сравнения с this.kind(). */
  readonly Kind = MedicalRecordKind;

  items: MedicalRecord[] = [];
  form = {
    personName: '',
    recordDate: '',
    doctor: '',
    description: '',
    familyDependentId: null as string | null,
    targetUserId: null as string | null,
  };
  error: string | null = null;
  loading = true;

  search!: DebouncedSearch<SearchResultItem>;

  // Вложения — с сервера (GET /api/medical-records/{id}/attachments), не из памяти сессии:
  // раньше список не переживал перезагрузку страницы (см. TECH_DEBT).
  attachmentsByRecord: Record<string, Attachment[]> = {};

  // L1: семьи, которым владелец глобально расшарил записи (общее для обоих видов — единый шаринг).
  shares: string[] = [];

  // Запись, для которой сейчас открыта шторка «Доступ» (null — шторка закрыта).
  accessRecord: MedicalRecord | null = null;

  // undefined — ещё ни разу не загружали.
  private loadedKind: MedicalRecordKind | undefined = undefined;

  constructor() {
    // Реагирует на смену вида, пока панель смонтирована (сейчас оба вида монтируются на разных
    // страницах, но контракт Panel требует этого независимо — см. medkits-panel.component.ts).
    effect(() => {
      const kind = this.kind();
      if (kind === this.loadedKind) return;
      this.resetForm();
      this.accessRecord = null;
      this.search = this.buildSearch(kind);
      void this.refresh();
    });
  }

  ngOnInit(): void {
    // Первичная загрузка — здесь, а не только в effect(): effect выполняется на следующем цикле
    // change detection и может не успеть отработать до первого рендера шаблона.
    if (this.kind() !== this.loadedKind) {
      this.search = this.buildSearch(this.kind());
      void this.refresh();
    }
  }

  get labels(): KindLabels {
    return KIND_LABELS[this.kind()];
  }

  /** «Я» + все подопечные и все другие активные участники из моих активных семей (дедуп по
   * userId — участник может состоять сразу в нескольких общих со мной семьях). */
  get patientOptions(): PatientOption[] {
    const options: PatientOption[] = [SELF_OPTION];
    const myUserId = this.auth.me()?.userId;
    const seenUserIds = new Set<string>(myUserId ? [myUserId] : []);

    for (const family of this.state.activeFamilies()) {
      for (const dep of family.dependents ?? []) {
        options.push({
          key: `dep:${dep.id}`,
          familyDependentId: dep.id,
          targetUserId: null,
          label: `${dep.name} (${family.name})`,
        });
      }
      for (const member of family.currentMembers ?? []) {
        if (seenUserIds.has(member.id)) continue;
        seenUserIds.add(member.id);
        options.push({
          key: `user:${member.id}`,
          familyDependentId: null,
          targetUserId: member.id,
          label: `${member.displayName} (${family.name})`,
        });
      }
    }
    return options;
  }

  get selectedPatientKey(): string {
    if (this.form.familyDependentId) return `dep:${this.form.familyDependentId}`;
    if (this.form.targetUserId) return `user:${this.form.targetUserId}`;
    return 'self';
  }

  /** Меняет familyDependentId/targetUserId по выбору и подставляет имя в форму для удобства —
   * поле остаётся редактируемым вручную дальше. */
  set selectedPatientKey(key: string) {
    const option = this.patientOptions.find((o) => o.key === key);
    this.form.familyDependentId = option?.familyDependentId ?? null;
    this.form.targetUserId = option?.targetUserId ?? null;
    if (option && option !== SELF_OPTION) {
      this.form.personName = option.label.replace(/\s*\([^)]*\)$/, '');
    }
  }

  private buildSearch(kind: MedicalRecordKind): DebouncedSearch<SearchResultItem> {
    return new DebouncedSearch<SearchResultItem>(
      (q) => this.api.search(q, SEARCH_TYPE_TOKEN[kind]).then((r) => r.items),
      (err) => (err instanceof ApiError ? err.message : 'Не удалось выполнить поиск.'),
    );
  }

  onSearchQueryChange(value: string): void {
    this.search.query = value;
    this.search.onQueryChange();
  }

  /** Поиск отдаёт только id + score (шифрованные поля не индексируются в БД) — рендерим уже
   * загруженные items, отфильтрованные и упорядоченные по совпавшим id. */
  get displayedItems(): MedicalRecord[] {
    if (this.search.query.trim().length < SEARCH_MIN_QUERY_LENGTH || !this.search.searched) {
      return this.items;
    }
    const scoreById = new Map(this.search.items.map((i) => [i.id, i.score]));
    return this.items
      .filter((r) => scoreById.has(r.id))
      .sort((a, b) => scoreById.get(b.id)! - scoreById.get(a.id)!);
  }

  async refresh(): Promise<void> {
    const kind = this.kind();
    this.loadedKind = kind;
    this.loading = true;
    try {
      const [items, shares] = await Promise.all([
        this.api.getMedicalRecords(LIST_KIND_TOKEN[kind]),
        this.api.getMedicalRecordShares(),
      ]);
      this.items = items;
      this.shares = shares;
      // Открытая шторка должна остаться синхронной с перезагруженным состоянием записи.
      if (this.accessRecord) {
        this.accessRecord = this.items.find((r) => r.id === this.accessRecord!.id) ?? null;
      }

      const pairs = await Promise.all(
        items.map(async (item) => [item.id, await this.api.getRecordAttachments(item.id)] as const),
      );
      this.attachmentsByRecord = Object.fromEntries(pairs);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить данные.';
    } finally {
      this.loading = false;
    }
  }

  async handleSubmit(): Promise<void> {
    if (!this.form.personName.trim() || !this.form.recordDate) return;
    try {
      await this.api.createMedicalRecord({
        kind: this.kind(),
        personName: this.form.personName.trim(),
        recordDate: this.form.recordDate,
        doctor: this.form.doctor.trim() || null,
        description: this.form.description.trim() || null,
        hideFromFamilyIds: null,
        familyDependentId: this.form.familyDependentId,
        targetUserId: this.form.targetUserId,
      });
      this.resetForm();
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось сохранить запись.';
    }
  }

  /** Безусловное удаление доступно только владельцу (кто физически загрузил) — сервер
   * перепроверит независимо от того, кому запись сейчас видна. */
  canDelete(record: MedicalRecord): boolean {
    return record.ownerUserId === this.auth.me()?.userId;
  }

  async handleDelete(record: MedicalRecord): Promise<void> {
    const confirmed = await this.confirm.confirm({
      title: 'Удалить запись?',
      message: 'Запись и все её вложения будут удалены безвозвратно.',
      confirmText: 'Удалить',
      danger: true,
    });
    if (!confirmed) return;

    try {
      await this.api.deleteMedicalRecord(record.id);
      if (this.accessRecord?.id === record.id) this.accessRecord = null;
      await this.refresh();
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось удалить запись.';
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
    } finally {
      input.value = '';
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
   * открывал записи. L1-шаринг общий на оба вида (единый шаринг «Анализы + Врачи»), поэтому это
   * затрагивает базовую видимость всех записей той же семье, а не только текущего вида — осознанно.
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

  private resetForm(): void {
    this.form = {
      personName: '',
      recordDate: '',
      doctor: '',
      description: '',
      familyDependentId: null,
      targetUserId: null,
    };
  }
}
