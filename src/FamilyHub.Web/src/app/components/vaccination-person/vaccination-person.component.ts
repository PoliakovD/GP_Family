import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiError, ApiService } from '../../services/api.service';
import { BreakpointService } from '../../services/breakpoint.service';
import { VaccinationStateService } from '../../services/vaccination-state.service';
import { ToastService } from '../../shared/toast/toast.service';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { AvatarComponent } from '../../shared/avatar/avatar.component';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { FileViewerComponent } from '../../shared/file-viewer/file-viewer.component';
import {
  Attachment, AttachmentPreviewStatus, VaccinationDatePrecision, VaccinationKind, VaccinationPersonSchedule,
  VaccinationScheduleItem, VaccinationSeriesDetail, VaccinationStatus,
} from '../../models/types';
import { ageText } from '../../shared/util/age';
import { preciseDateText, stageLabel, statusColor, statusIcon, statusLabel, windowToText } from '../../shared/util/vaccination-labels';
import { todayLocal } from '../../shared/util/intake-labels';

const STAGE_ORDER = ['0-1', '1-2', '2-6', '6-7', '14+', 'adult', 'epidemic', 'closed'];

interface StageGroup {
  stage: string;
  items: VaccinationScheduleItem[];
  allDone: boolean;
}

/**
 * График человека (макет «Screen - Vaccination»): «По возрасту» — этапы сворачиваются, кроме
 * текущего; «По болезням» — плоские группы. Деталь серии — справа сплитом на десктопе (как
 * intake-courses/:id), отдельным экраном на мобиле (тот же компонент, `code` — необязательный
 * component-bound параметр маршрута `.../series/:code`).
 */
@Component({
  selector: 'app-vaccination-person',
  imports: [AvatarComponent, FileViewerComponent, LoadingSpinnerComponent, RouterLink],
  templateUrl: './vaccination-person.component.html',
  styleUrl: './vaccination-person.component.scss',
})
export class VaccinationPersonComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly breakpoints = inject(BreakpointService);
  protected readonly vaccinations = inject(VaccinationStateService);

  readonly kind = input.required<'user' | 'dependent'>();
  readonly id = input.required<string>();
  readonly code = input<string | undefined>(undefined);

  protected readonly Status = VaccinationStatus;
  protected readonly statusIcon = statusIcon;
  protected readonly statusColor = statusColor;
  protected readonly statusLabel = statusLabel;
  protected readonly stageLabel = stageLabel;
  protected readonly windowToText = windowToText;
  protected readonly preciseDateText = preciseDateText;
  protected readonly ageText = ageText;

  readonly schedule = signal<VaccinationPersonSchedule | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly view = signal<'age' | 'disease'>('age');
  readonly openStages = signal<Set<string>>(new Set());
  readonly busyKey = signal<string | null>(null);
  readonly certificateBusy = signal(false);

  readonly seriesDetail = signal<VaccinationSeriesDetail | null>(null);
  readonly seriesLoading = signal(false);
  readonly seriesError = signal<string | null>(null);
  readonly viewerFiles = signal<Attachment[] | null>(null);
  readonly viewerIndex = signal(0);

  protected readonly isWide = computed(() => this.breakpoints.tier() === 'wide');
  protected readonly showList = computed(() => this.isWide() || !this.code());
  protected readonly showDetail = computed(() => !!this.code());

  protected readonly ageDisplay = computed(() => {
    const s = this.schedule();
    if (!s || s.ageYears === null) return null;
    return `${s.ageYears} ${s.ageYears === 1 ? 'год' : s.ageYears < 5 ? 'года' : 'лет'}${s.ageMonths ? ` ${s.ageMonths} мес` : ''}`;
  });

  protected readonly stageGroups = computed<StageGroup[]>(() => {
    const s = this.schedule();
    if (!s) return [];
    const byStage = new Map<string, VaccinationScheduleItem[]>();
    for (const item of s.byAge) {
      if (!byStage.has(item.stage)) byStage.set(item.stage, []);
      byStage.get(item.stage)!.push(item);
    }
    return STAGE_ORDER
      .filter((stage) => byStage.has(stage))
      .map((stage) => {
        const items = byStage.get(stage)!;
        const allDone = items.every((i) => i.status === VaccinationStatus.Done || i.status === VaccinationStatus.HadDisease);
        return { stage, items, allDone };
      });
  });

  constructor() {
    effect(() => {
      const k = this.kind();
      const i = this.id();
      untracked(() => void this.loadSchedule(k, i));
    });

    effect(() => {
      this.vaccinations.version();
      untracked(() => void this.loadSchedule(this.kind(), this.id()));
    });

    // Текущий этап (первый не полностью пройденный) открыт по умолчанию — «прошедшие этапы свёрнуты».
    effect(() => {
      const groups = this.stageGroups();
      untracked(() => {
        if (this.openStages().size > 0 || groups.length === 0) return;
        const current = groups.find((g) => !g.allDone) ?? groups[groups.length - 1];
        this.openStages.set(new Set([current.stage]));
      });
    });

    effect(() => {
      const code = this.code();
      const k = this.kind();
      const i = this.id();
      untracked(() => {
        if (code) void this.loadSeries(k, i, code);
        else this.seriesDetail.set(null);
      });
    });
  }

  protected isOpen(stage: string): boolean {
    return this.openStages().has(stage);
  }

  protected toggleStage(stage: string): void {
    this.openStages.update((set) => {
      const next = new Set(set);
      if (next.has(stage)) next.delete(stage);
      else next.add(stage);
      return next;
    });
  }

  protected doneCount(items: VaccinationScheduleItem[]): number {
    return items.filter((i) => i.status === VaccinationStatus.Done || i.status === VaccinationStatus.HadDisease).length;
  }

  protected openSeries(item: VaccinationScheduleItem): void {
    if (!item.seriesCode) return; // не из календаря — своя карточка, не серия
    void this.router.navigate(['/health/vaccinations/people', this.kind(), this.id(), 'series', item.seriesCode]);
  }

  protected backToList(): void {
    void this.router.navigate(['/health/vaccinations/people', this.kind(), this.id()]);
  }

  protected itemKey(item: VaccinationScheduleItem): string {
    return `${item.seriesCode}:${item.doseIndex}`;
  }

  protected async quickMarkDone(item: VaccinationScheduleItem, event: Event): Promise<void> {
    event.stopPropagation();
    const key = this.itemKey(item);
    this.busyKey.set(key);
    try {
      await this.api.createVaccination({
        subjectKind: this.kind(),
        subjectId: this.id(),
        seriesCode: item.seriesCode,
        doseIndex: item.doseIndex,
        customName: null,
        vaccineName: null,
        kind: VaccinationKind.Done,
        date: todayLocal(),
        datePrecision: VaccinationDatePrecision.Day,
        certificateId: null,
        requestWellbeingCheck: false,
      });
      this.vaccinations.showSaved({
        subject: this.schedule()!.subject, item: { ...item, status: VaccinationStatus.Done, date: todayLocal() },
        reactionHint: this.vaccinations.reactionHintFor(item.seriesCode),
      });
      this.vaccinations.changed();
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось сохранить прививку.');
    } finally {
      this.busyKey.set(null);
    }
  }

  protected async deleteCustom(item: VaccinationScheduleItem, event: Event): Promise<void> {
    event.stopPropagation();
    if (!item.recordId) return;
    const ok = await this.confirm.confirm({ title: 'Удалить запись?', message: `«${item.seriesName}» будет удалена.`, danger: true });
    if (!ok) return;
    try {
      await this.api.deleteVaccination(item.recordId);
      this.toast.success('Запись удалена.');
      this.vaccinations.changed();
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось удалить запись.');
    }
  }

  protected async openFiles(files: { id: string; fileName: string; sizeBytes: number; uploadedAt: string }[], index = 0): Promise<void> {
    this.viewerFiles.set(files.map((f) => ({
      id: f.id, fileName: f.fileName, contentType: '', sizeBytes: f.sizeBytes, uploadedAt: f.uploadedAt,
      extractedAt: null, previewStatus: AttachmentPreviewStatus.Ready,
    })));
    this.viewerIndex.set(index);
  }

  /** Скачивание — через HttpClient (в Telegram нет cookie): blob + синтетический <a download>. */
  protected async downloadCertificate(): Promise<void> {
    this.certificateBusy.set(true);
    try {
      const blob = await this.api.getVaccinationCertificatePdf(this.kind(), this.id());
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `sertifikat-privivok-${todayLocal()}.pdf`;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось сформировать сертификат.');
    } finally {
      this.certificateBusy.set(false);
    }
  }

  protected addForThisPerson(): void {
    this.vaccinations.openAdd({ subjectKind: this.kind(), subjectId: this.id() });
  }

  private async loadSchedule(kind: 'user' | 'dependent', id: string): Promise<void> {
    this.loading.set(true);
    try {
      this.schedule.set(await this.api.getVaccinationPersonSchedule(kind, id));
      this.error.set(null);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Не удалось загрузить график.');
    } finally {
      this.loading.set(false);
    }
  }

  private async loadSeries(kind: 'user' | 'dependent', id: string, code: string): Promise<void> {
    this.seriesLoading.set(true);
    this.seriesError.set(null);
    try {
      this.seriesDetail.set(await this.api.getVaccinationSeriesDetail(kind, id, code));
    } catch (e) {
      this.seriesError.set(e instanceof ApiError ? e.message : 'Не удалось загрузить прививку.');
    } finally {
      this.seriesLoading.set(false);
    }
  }
}
