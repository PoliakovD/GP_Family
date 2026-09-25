import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import {
  AdminApiService,
  AdminKbEditRequest,
  AdminLabAnalyteDetail,
  AdminMedicationDetail,
  GlobalSpecimen,
  KbAnalyteListItem,
  KbListItem,
} from '../../../services/admin-api.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { AdminPayloadEditorComponent, PayloadSaveEvent } from '../admin-payload-editor/admin-payload-editor.component';

const PAGE_SIZE = 20;

type CatalogTab = 'analytes' | 'medications' | 'specimens';

/**
 * Ручная правка справочников после ИИ из админки (§3/§10 плана) — показатели, медикаменты,
 * источники. Каждое сохранённое поле (имя/payload/алиасы) автоматически лочится — следующий
 * проход автообогащения его не тронет (см. class doc AdminCatalogService на бэкенде). Payload
 * редактируется через `app-admin-payload-editor` — тумблер «Форма ⇄ JSON»: форма лочит только
 * реально изменённые "payload.&lt;key&gt;", JSON — payload целиком (тот же выбор, что раньше был
 * единственным, теперь ветка на случай схемы/полей вне формы).
 */
@Component({
    selector: 'app-admin-catalog',
    imports: [FormsModule, DatePipe, AdminPayloadEditorComponent],
    templateUrl: './admin-catalog.component.html'
})
export class AdminCatalogComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  private readonly route = inject(ActivatedRoute);

  /** Какая из трёх страниц справочника открыта — задаётся роутом (`data.tab`, см. admin.routes.ts),
   * переключатель между ними — второй уровень навигации раздела (AdminSectionComponent). */
  readonly tab = signal<CatalogTab>(this.route.snapshot.data['tab'] ?? 'analytes');

  // --- Показатели ---
  readonly analyteQuery = signal('');
  readonly analytes = signal<KbAnalyteListItem[]>([]);
  readonly analytesLoading = signal(false);
  readonly analyteDetail = signal<AdminLabAnalyteDetail | null>(null);
  readonly analyteEditorDisplayName = signal('');
  readonly analyteEditorAliases = signal('');
  readonly analyteBusy = signal(false);
  /** Мердж дублей (§ ручной мердж): id строки, отмеченной как "проигравшая" — следующий клик
   * "Слить сюда" на другой строке того же списка довершает мердж. null — режим мерджа не начат. */
  readonly analyteMergeSourceId = signal<string | null>(null);

  // --- Медикаменты ---
  readonly medicationQuery = signal('');
  readonly medications = signal<KbListItem[]>([]);
  readonly medicationsLoading = signal(false);
  readonly medicationDetail = signal<AdminMedicationDetail | null>(null);
  readonly medicationEditorDisplayName = signal('');
  readonly medicationEditorAliases = signal('');
  readonly medicationBusy = signal(false);

  // --- Источники ---
  readonly specimenQuery = signal('');
  readonly specimens = signal<GlobalSpecimen[]>([]);
  readonly specimensLoading = signal(false);
  readonly specimenRenameDrafts = signal<Record<string, string>>({});
  readonly specimenBusy = signal(false);
  /** Мердж дублей (реальный кейс: "Эякулят"/"Физические свойства Эякулят" — три строки вместо
   * одной, см. class doc GlobalSpecimenKbService.MergeAsync на бэкенде) — та же двухкликовая
   * схема, что у показателей: отметить проигравшего, затем кликнуть "Слить сюда" на победителе. */
  readonly specimenMergeSourceId = signal<string | null>(null);

  ngOnInit(): void {
    const tab = this.tab();
    if (tab === 'analytes') void this.searchAnalytes();
    else if (tab === 'medications') void this.searchMedications();
    else void this.searchSpecimens();
  }

  // --- Показатели ---

  async searchAnalytes(): Promise<void> {
    this.analytesLoading.set(true);
    try {
      const page = await this.api.searchLabAnalytes(this.analyteQuery(), 0, PAGE_SIZE);
      this.analytes.set(page.items);
    } catch {
      this.toast.error('Не удалось загрузить список показателей.');
    } finally {
      this.analytesLoading.set(false);
    }
  }

  async openAnalyte(item: KbAnalyteListItem): Promise<void> {
    await this.openAnalyteById(item.id);
  }

  /** Переход по ссылке «Что смотрят вместе» из редактора payload — та же загрузка, что
   * openAnalyte, но по одному id, без строки списка (клик мог прийти из статьи, которой сейчас
   * нет в текущей выдаче поиска). */
  async openAnalyteById(id: string): Promise<void> {
    try {
      const detail = await this.api.getLabAnalyte(id);
      this.analyteDetail.set(detail);
      this.analyteEditorDisplayName.set(detail.displayName);
      this.analyteEditorAliases.set(detail.aliases.join(', '));
    } catch {
      this.toast.error('Не удалось загрузить показатель.');
    }
  }

  async saveAnalyteField(field: 'displayName' | 'aliases'): Promise<void> {
    const detail = this.analyteDetail();
    if (!detail) return;

    this.analyteBusy.set(true);
    try {
      const request: AdminKbEditRequest =
        field === 'displayName'
          ? { displayName: this.analyteEditorDisplayName() }
          : { aliases: this.splitAliases(this.analyteEditorAliases()) };

      this.analyteDetail.set(await this.api.updateLabAnalyte(detail.id, request));
      this.toast.success('Сохранено и залочено.');
      await this.searchAnalytes();
    } catch {
      this.toast.error('Не удалось сохранить — проверьте текст на персональные данные.');
    } finally {
      this.analyteBusy.set(false);
    }
  }

  /** См. app-admin-payload-editor.PayloadSaveEvent — changedKeys=null (режим JSON) лочит payload
   * целиком, changedKeys=[] (режим формы) лочит только реально изменённые "payload.&lt;key&gt;". */
  async onAnalytePayloadSave(event: PayloadSaveEvent): Promise<void> {
    const detail = this.analyteDetail();
    if (!detail) return;

    this.analyteBusy.set(true);
    try {
      const request: AdminKbEditRequest = { payloadJson: event.payloadJson };
      if (event.changedKeys !== null) request.lockedPayloadKeys = event.changedKeys;

      this.analyteDetail.set(await this.api.updateLabAnalyte(detail.id, request));
      this.toast.success('Сохранено и залочено.');
      await this.searchAnalytes();
    } catch {
      this.toast.error('Не удалось сохранить — проверьте JSON и текст на персональные данные.');
    } finally {
      this.analyteBusy.set(false);
    }
  }

  async unlockAnalyteField(field: string): Promise<void> {
    const detail = this.analyteDetail();
    if (!detail) return;

    this.analyteBusy.set(true);
    try {
      await this.api.unlockLabAnalyteField(detail.id, field);
      this.analyteDetail.set(await this.api.getLabAnalyte(detail.id));
    } catch {
      this.toast.error('Не удалось снять замок.');
    } finally {
      this.analyteBusy.set(false);
    }
  }

  async reenrichAnalyte(): Promise<void> {
    const detail = this.analyteDetail();
    if (!detail) return;

    this.analyteBusy.set(true);
    try {
      await this.api.reenrichLabAnalyte(detail.id);
      this.toast.success('Переобогащение поставлено в очередь.');
    } catch {
      this.toast.error('Не удалось поставить переобогащение.');
    } finally {
      this.analyteBusy.set(false);
    }
  }

  /** Клик "Начать мердж"/"Отмена"/"Слить сюда" на строке списка (см. analyteMergeSourceId). */
  async onAnalyteMergeClick(item: KbAnalyteListItem): Promise<void> {
    const sourceId = this.analyteMergeSourceId();
    if (sourceId === null) {
      this.analyteMergeSourceId.set(item.id);
      return;
    }
    if (sourceId === item.id) {
      this.analyteMergeSourceId.set(null);
      return;
    }

    const loser = this.analytes().find((a) => a.id === sourceId);
    const ok = await this.confirm.confirm({
      title: 'Объединить показатели?',
      message: `«${loser?.displayName ?? sourceId}» будет удалён, его показатели пользователей и синонимы переедут на «${item.displayName}».`,
      confirmText: 'Объединить',
      danger: true,
    });
    if (!ok) return;

    this.analyteBusy.set(true);
    try {
      await this.api.mergeLabAnalytes(sourceId, item.id);
      this.toast.success('Показатели объединены.');
      this.analyteMergeSourceId.set(null);
      if (this.analyteDetail()?.id === sourceId) this.analyteDetail.set(null);
      await this.searchAnalytes();
    } catch {
      this.toast.error('Не удалось объединить показатели.');
    } finally {
      this.analyteBusy.set(false);
    }
  }

  async deleteAnalyte(): Promise<void> {
    const detail = this.analyteDetail();
    if (!detail) return;

    const ok = await this.confirm.confirm({
      title: 'Удалить показатель из справочника?',
      message: `«${detail.displayName}» будет удалён из общего справочника. Показатели пользователей, уже привязанные к нему, потеряют ссылку на статью.`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;

    this.analyteBusy.set(true);
    try {
      await this.api.deleteLabAnalyte(detail.id);
      this.analyteDetail.set(null);
      await this.searchAnalytes();
    } catch {
      this.toast.error('Не удалось удалить.');
    } finally {
      this.analyteBusy.set(false);
    }
  }

  // --- Медикаменты ---

  async searchMedications(): Promise<void> {
    this.medicationsLoading.set(true);
    try {
      const page = await this.api.searchMedications(this.medicationQuery(), 0, PAGE_SIZE);
      this.medications.set(page.items);
    } catch {
      this.toast.error('Не удалось загрузить список медикаментов.');
    } finally {
      this.medicationsLoading.set(false);
    }
  }

  async openMedication(item: KbListItem): Promise<void> {
    try {
      const detail = await this.api.getMedication(item.id);
      this.medicationDetail.set(detail);
      this.medicationEditorDisplayName.set(detail.displayName);
      this.medicationEditorAliases.set(detail.aliases.join(', '));
    } catch {
      this.toast.error('Не удалось загрузить медикамент.');
    }
  }

  async saveMedicationField(field: 'displayName' | 'aliases'): Promise<void> {
    const detail = this.medicationDetail();
    if (!detail) return;

    this.medicationBusy.set(true);
    try {
      const request: AdminKbEditRequest =
        field === 'displayName'
          ? { displayName: this.medicationEditorDisplayName() }
          : { aliases: this.splitAliases(this.medicationEditorAliases()) };

      this.medicationDetail.set(await this.api.updateMedication(detail.id, request));
      this.toast.success('Сохранено и залочено.');
      await this.searchMedications();
    } catch {
      this.toast.error('Не удалось сохранить — проверьте текст на персональные данные.');
    } finally {
      this.medicationBusy.set(false);
    }
  }

  async onMedicationPayloadSave(event: PayloadSaveEvent): Promise<void> {
    const detail = this.medicationDetail();
    if (!detail) return;

    this.medicationBusy.set(true);
    try {
      const request: AdminKbEditRequest = { payloadJson: event.payloadJson };
      if (event.changedKeys !== null) request.lockedPayloadKeys = event.changedKeys;

      this.medicationDetail.set(await this.api.updateMedication(detail.id, request));
      this.toast.success('Сохранено и залочено.');
      await this.searchMedications();
    } catch {
      this.toast.error('Не удалось сохранить — проверьте JSON и текст на персональные данные.');
    } finally {
      this.medicationBusy.set(false);
    }
  }

  async unlockMedicationField(field: string): Promise<void> {
    const detail = this.medicationDetail();
    if (!detail) return;

    this.medicationBusy.set(true);
    try {
      await this.api.unlockMedicationField(detail.id, field);
      this.medicationDetail.set(await this.api.getMedication(detail.id));
    } catch {
      this.toast.error('Не удалось снять замок.');
    } finally {
      this.medicationBusy.set(false);
    }
  }

  async deleteMedication(): Promise<void> {
    const detail = this.medicationDetail();
    if (!detail) return;

    const ok = await this.confirm.confirm({
      title: 'Удалить медикамент из справочника?',
      message: `«${detail.displayName}» будет удалён из общего справочника.`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;

    this.medicationBusy.set(true);
    try {
      await this.api.deleteMedication(detail.id);
      this.medicationDetail.set(null);
      await this.searchMedications();
    } catch {
      this.toast.error('Не удалось удалить.');
    } finally {
      this.medicationBusy.set(false);
    }
  }

  // --- Источники ---

  async searchSpecimens(): Promise<void> {
    this.specimensLoading.set(true);
    try {
      this.specimens.set(await this.api.searchSpecimens(this.specimenQuery()));
    } catch {
      this.toast.error('Не удалось загрузить список источников.');
    } finally {
      this.specimensLoading.set(false);
    }
  }

  specimenDraft(s: GlobalSpecimen): string {
    return this.specimenRenameDrafts()[s.id] ?? s.displayName;
  }

  setSpecimenDraft(id: string, value: string): void {
    this.specimenRenameDrafts.update((d) => ({ ...d, [id]: value }));
  }

  async renameSpecimen(s: GlobalSpecimen): Promise<void> {
    const draft = this.specimenDraft(s).trim();
    if (!draft || draft === s.displayName) return;

    this.specimenBusy.set(true);
    try {
      await this.api.renameSpecimen(s.id, draft);
      this.toast.success('Переименовано.');
      await this.searchSpecimens();
    } catch {
      this.toast.error('Не удалось переименовать — возможно, такое название уже есть.');
    } finally {
      this.specimenBusy.set(false);
    }
  }

  /** Клик "Начать мердж"/"Отмена"/"Слить сюда" на строке списка (см. specimenMergeSourceId). */
  async onSpecimenMergeClick(item: GlobalSpecimen): Promise<void> {
    const sourceId = this.specimenMergeSourceId();
    if (sourceId === null) {
      this.specimenMergeSourceId.set(item.id);
      return;
    }
    if (sourceId === item.id) {
      this.specimenMergeSourceId.set(null);
      return;
    }

    const loser = this.specimens().find((s) => s.id === sourceId);
    const ok = await this.confirm.confirm({
      title: 'Объединить источники?',
      message: `«${loser?.displayName ?? sourceId}» будет удалён, все показатели/статьи справочника, использующие его, переедут на «${item.displayName}», а старое название станет синонимом.`,
      confirmText: 'Объединить',
      danger: true,
    });
    if (!ok) return;

    this.specimenBusy.set(true);
    try {
      await this.api.mergeSpecimens(sourceId, item.id);
      this.toast.success('Источники объединены.');
      this.specimenMergeSourceId.set(null);
      await this.searchSpecimens();
    } catch {
      this.toast.error('Не удалось объединить источники.');
    } finally {
      this.specimenBusy.set(false);
    }
  }

  async deleteSpecimen(s: GlobalSpecimen): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Удалить источник?',
      message: `«${s.displayName}» будет удалён из справочника источников. Заблокировано, если источник ещё используется.`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;

    this.specimenBusy.set(true);
    try {
      await this.api.deleteSpecimen(s.id);
      await this.searchSpecimens();
    } catch {
      this.toast.error('Не удалось удалить — источник используется.');
    } finally {
      this.specimenBusy.set(false);
    }
  }

  private splitAliases(raw: string): string[] {
    return raw.split(',').map((a) => a.trim()).filter((a) => a.length > 0);
  }

  /** "payload.<key>" -> "<key>" — гранулярные локи (§4/§10 плана) рядом с локом всего "payload"
   * в том же массиве LockedFields. */
  partialPayloadLocks(lockedFields: readonly string[]): string[] {
    return lockedFields.filter((f) => f.startsWith('payload.')).map((f) => f.slice('payload.'.length));
  }
}
