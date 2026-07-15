import { Component, Input, OnInit, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import { TelegramService } from '../../services/telegram.service';
import type { Attachment, FamilySummary, MedicalRecord } from '../../models/types';

@Component({
  selector: 'app-medical-records-tab',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './medical-records-tab.component.html',
})
export class MedicalRecordsTabComponent implements OnInit {
  @Input() activeFamilies: FamilySummary[] = [];

  private readonly api = inject(ApiService);
  private readonly tg = inject(TelegramService);

  items: MedicalRecord[] = [];
  form = { personName: '', recordDate: '', doctor: '', description: '' };
  error: string | null = null;
  shareFamilyByRecord: Record<string, string> = {};
  // Бэкенд не отдаёт список вложений отдельным эндпоинтом — храним то, что
  // загрузили в текущей сессии (ответ POST .../attachments содержит Attachment целиком).
  attachmentsByRecord: Record<string, Attachment[]> = {};

  ngOnInit(): void {
    this.refresh();
  }

  async refresh(): Promise<void> {
    try {
      this.items = await this.api.getMedicalRecords();
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить анализы.';
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

  async handleShare(recordId: string, share: boolean): Promise<void> {
    const familyId = this.shareFamilyByRecord[recordId];
    if (!familyId) return;
    try {
      if (share) {
        await this.api.shareMedicalRecord(familyId);
      } else {
        await this.api.unshareMedicalRecord(familyId);
      }
      this.error = null;
    } catch (err) {
      this.error =
        err instanceof ApiError ? err.message : 'Действие доступно только владельцу записи.';
    }
  }

  async handleHide(recordId: string, hide: boolean): Promise<void> {
    const familyId = this.shareFamilyByRecord[recordId];
    if (!familyId) return;
    try {
      if (hide) {
        await this.api.hideMedicalRecord(recordId, [familyId]);
      } else {
        await this.api.unhideMedicalRecord(recordId, [familyId]);
      }
      this.error = null;
    } catch (err) {
      this.error =
        err instanceof ApiError ? err.message : 'Действие доступно только владельцу записи.';
    }
  }

  setShareFamily(recordId: string, familyId: string): void {
    this.shareFamilyByRecord = { ...this.shareFamilyByRecord, [recordId]: familyId };
  }

  attachmentsFor(recordId: string): Attachment[] {
    return this.attachmentsByRecord[recordId] ?? [];
  }

  shareFamilyFor(recordId: string): string {
    return this.shareFamilyByRecord[recordId] ?? '';
  }
}
