import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  AdminApiService,
  AdminLabAnalyteDetail,
  AdminMedicationDetail,
  GlobalSpecimen,
  KbAnalyteListItem,
  KbListItem,
} from '../../../services/admin-api.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';

const PAGE_SIZE = 20;

/**
 * Ручная правка справочников после ИИ из админки (§3 плана) — показатели, медикаменты,
 * источники. Каждое сохранённое поле (имя/payload/алиасы) автоматически лочится — следующий
 * проход автообогащения его не тронет (см. class doc AdminCatalogService на бэкенде). Payload
 * редактируется как сырой JSON-текст (тот же выбор, что редактор промптов) — проще и честнее
 * формы по каждому подполю: автообогащение всё равно пишет payload целиком.
 */
@Component({
  selector: 'app-admin-catalog',
  standalone: true,
  imports: [FormsModule, DatePipe],
  templateUrl: './admin-catalog.component.html',
})
export class AdminCatalogComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly tab = signal<'analytes' | 'medications' | 'specimens'>('analytes');

  // --- Показатели ---
  readonly analyteQuery = signal('');
  readonly analytes = signal<KbAnalyteListItem[]>([]);
  readonly analytesLoading = signal(false);
  readonly analyteDetail = signal<AdminLabAnalyteDetail | null>(null);
  readonly analyteEditorDisplayName = signal('');
  readonly analyteEditorPayload = signal('');
  readonly analyteEditorAliases = signal('');
  readonly analyteBusy = signal(false);

  // --- Медикаменты ---
  readonly medicationQuery = signal('');
  readonly medications = signal<KbListItem[]>([]);
  readonly medicationsLoading = signal(false);
  readonly medicationDetail = signal<AdminMedicationDetail | null>(null);
  readonly medicationEditorDisplayName = signal('');
  readonly medicationEditorPayload = signal('');
  readonly medicationEditorAliases = signal('');
  readonly medicationBusy = signal(false);

  // --- Источники ---
  readonly specimenQuery = signal('');
  readonly specimens = signal<GlobalSpecimen[]>([]);
  readonly specimensLoading = signal(false);
  readonly specimenRenameDrafts = signal<Record<string, string>>({});
  readonly specimenBusy = signal(false);

  ngOnInit(): void {
    void this.searchAnalytes();
  }

  selectTab(tab: 'analytes' | 'medications' | 'specimens'): void {
    this.tab.set(tab);
    if (tab === 'analytes' && this.analytes().length === 0) void this.searchAnalytes();
    if (tab === 'medications' && this.medications().length === 0) void this.searchMedications();
    if (tab === 'specimens' && this.specimens().length === 0) void this.searchSpecimens();
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
    try {
      const detail = await this.api.getLabAnalyte(item.id);
      this.analyteDetail.set(detail);
      this.analyteEditorDisplayName.set(detail.displayName);
      this.analyteEditorPayload.set(this.prettyJson(detail.payloadJson));
      this.analyteEditorAliases.set(detail.aliases.join(', '));
    } catch {
      this.toast.error('Не удалось загрузить показатель.');
    }
  }

  async saveAnalyteField(field: 'displayName' | 'payloadJson' | 'aliases'): Promise<void> {
    const detail = this.analyteDetail();
    if (!detail) return;

    this.analyteBusy.set(true);
    try {
      const request =
        field === 'displayName'
          ? { displayName: this.analyteEditorDisplayName() }
          : field === 'payloadJson'
            ? { payloadJson: this.analyteEditorPayload() }
            : { aliases: this.splitAliases(this.analyteEditorAliases()) };

      const updated = await this.api.updateLabAnalyte(detail.id, request);
      this.analyteDetail.set(updated);
      this.analyteEditorPayload.set(this.prettyJson(updated.payloadJson));
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
      this.medicationEditorPayload.set(this.prettyJson(detail.payloadJson));
      this.medicationEditorAliases.set(detail.aliases.join(', '));
    } catch {
      this.toast.error('Не удалось загрузить медикамент.');
    }
  }

  async saveMedicationField(field: 'displayName' | 'payloadJson' | 'aliases'): Promise<void> {
    const detail = this.medicationDetail();
    if (!detail) return;

    this.medicationBusy.set(true);
    try {
      const request =
        field === 'displayName'
          ? { displayName: this.medicationEditorDisplayName() }
          : field === 'payloadJson'
            ? { payloadJson: this.medicationEditorPayload() }
            : { aliases: this.splitAliases(this.medicationEditorAliases()) };

      const updated = await this.api.updateMedication(detail.id, request);
      this.medicationDetail.set(updated);
      this.medicationEditorPayload.set(this.prettyJson(updated.payloadJson));
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

  private prettyJson(json: string): string {
    try {
      return JSON.stringify(JSON.parse(json), null, 2);
    } catch {
      return json;
    }
  }
}
