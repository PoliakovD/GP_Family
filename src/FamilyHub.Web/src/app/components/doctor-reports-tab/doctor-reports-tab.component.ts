import { Component, Input, OnInit, computed, inject, signal } from '@angular/core';
import { ApiService } from '../../services/api.service';
import { BreakpointService } from '../../services/breakpoint.service';
import { TelegramService } from '../../services/telegram.service';
import { DoctorReport, DoctorReportLinkStatus } from '../../models/types';
import { ActionMenuComponent, ActionMenuItem } from '../../shared/action-menu/action-menu.component';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { ConfirmService } from '../../shared/confirm/confirm.service';
import { FileViewerComponent } from '../../shared/file-viewer/file-viewer.component';
import { SidePanelComponent } from '../../shared/side-panel/side-panel.component';
import { ToastService } from '../../shared/toast/toast.service';
import { copyToClipboard } from '../../shared/util/clipboard';
import { dayMonthFromIso, blocksLabel, linkView, LinkView, periodTitle, reportUrl } from '../../shared/util/report-labels';
import { shareLink } from '../../shared/util/share-link';
import { DoctorReportFormComponent } from '../doctor-report-form/doctor-report-form.component';
import { DoctorReportLinkDialogComponent } from '../doctor-report-link-dialog/doctor-report-link-dialog.component';

interface ReportRow {
  report: DoctorReport;
  title: string;
  meta: string;
  link: LinkView;
  primary: { label: string; icon: string; run: () => void };
  actions: ActionMenuItem[];
}

/**
 * Страница «Отчёты для врача» (вкладка хаба «Здоровье», макет «Screen - Doctor report»): список
 * PDF-отчётов, где главное в строке — статус публичной ссылки (отчёт — это медданные вне приложения,
 * пользователь должен видеть, у кого они открыты). Опасное («Отозвать», «Удалить») — только в меню «…».
 */
@Component({
  selector: 'app-doctor-reports-tab',
  imports: [
    ActionMenuComponent, BottomSheetComponent, DoctorReportFormComponent, DoctorReportLinkDialogComponent,
    FileViewerComponent, SidePanelComponent,
  ],
  templateUrl: './doctor-reports-tab.component.html',
  styleUrl: './doctor-reports-tab.component.scss',
})
export class DoctorReportsTabComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly tg = inject(TelegramService);
  private readonly breakpoints = inject(BreakpointService);

  /** `?new=1` — сразу открыть создание (кнопка «В отчёт для врача» в дневнике); `?diary=1` — с блоками дневника. */
  @Input() new?: string;
  @Input() diary?: string;

  readonly reports = signal<DoctorReport[]>([]);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);

  readonly formOpen = signal(false);
  readonly formFromDiary = signal(false);
  /** Отчёт, для которого открыт диалог ссылки; null — закрыт. */
  readonly linkTarget = signal<DoctorReport | null>(null);
  readonly linkShowResult = signal(false);
  readonly viewerFile = signal<File | null>(null);

  protected readonly isWide = computed(() => this.breakpoints.tier() === 'wide');

  protected readonly rows = computed<ReportRow[]>(() => this.reports().map((r) => this.toRow(r)));

  ngOnInit(): void {
    void this.load();
    if (this.new === '1') this.openForm(this.diary === '1');
  }

  protected openForm(fromDiary = false): void {
    this.formFromDiary.set(fromDiary);
    this.formOpen.set(true);
  }

  protected closeForm(): void {
    this.formOpen.set(false);
  }

  protected onCreated(report: DoctorReport): void {
    this.closeForm();
    this.toast.success('Отчёт сформирован');
    void this.load();
    // Сразу со ссылкой — показываем её (скопировать/поделиться), пока пользователь не ушёл со страницы.
    if (report.link.status === DoctorReportLinkStatus.Active) {
      this.openLinkDialog(report, true);
    }
  }

  protected openLinkDialog(report: DoctorReport, showResult = false): void {
    this.linkShowResult.set(showResult);
    this.linkTarget.set(report);
  }

  protected closeLinkDialog(): void {
    this.linkTarget.set(null);
    this.linkShowResult.set(false);
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      this.reports.set(await this.api.getDoctorReports());
    } catch {
      this.loadError.set('Не удалось загрузить отчёты. Проверьте соединение и попробуйте ещё раз.');
    } finally {
      this.loading.set(false);
    }
  }

  private toRow(report: DoctorReport): ReportRow {
    const link = linkView(report);
    const meta = [
      `Создан ${dayMonthFromIso(report.createdAt)}`,
      ...(report.recipient ? [`для ${report.recipient}`] : []),
      blocksLabel(report.blockCount),
    ].join(' · ');

    const active = report.link.status === DoctorReportLinkStatus.Active;
    const primary = active
      ? { label: 'Копировать ссылку', icon: 'ph ph-copy', run: () => void this.copyLink(report) }
      : {
          label: report.link.status === DoctorReportLinkStatus.None ? 'Создать ссылку' : 'Новая ссылка',
          icon: 'ph ph-link-simple',
          run: () => this.openLinkDialog(report),
        };

    const actions: ActionMenuItem[] = [
      { label: 'Открыть PDF', icon: 'ph ph-eye', handler: () => void this.openPdf(report) },
      ...(active ? [{ label: 'Продлить ссылку', icon: 'ph ph-clock-clockwise', handler: () => this.openLinkDialog(report) }] : []),
      ...(active ? [{ label: 'Отозвать доступ', icon: 'ph ph-link-break', danger: true, handler: () => void this.revoke(report) }] : []),
      { label: 'Удалить отчёт', icon: 'ph ph-trash', danger: true, handler: () => void this.remove(report) },
    ];

    return { report, title: periodTitle(report.periodFrom, report.periodTo), meta, link, primary, actions };
  }

  // ---- Действия ----

  protected async copyLink(report: DoctorReport): Promise<void> {
    if (!report.link.token) return;
    if (await copyToClipboard(reportUrl(report.link.token))) this.toast.success('Ссылка скопирована в буфер обмена.');
    else this.toast.error('Не удалось скопировать ссылку.');
  }

  protected async shareLink(report: DoctorReport): Promise<void> {
    if (!report.link.token) return;
    const url = reportUrl(report.link.token);
    const handled = await shareLink(this.tg, url, 'Отчёт для врача', 'Мой отчёт для врача (FamilyHub)');
    if (!handled) await this.copyLink(report);
  }

  /** Скачивание — через HttpClient (в Telegram нет cookie): blob + синтетический <a download>. */
  protected async download(report: DoctorReport): Promise<void> {
    try {
      const blob = await this.api.downloadDoctorReportPdf(report.id);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `otchet-dlya-vracha-${report.periodFrom}-${report.periodTo}.pdf`;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    } catch {
      this.toast.error('Не удалось скачать PDF');
    }
  }

  protected async openPdf(report: DoctorReport): Promise<void> {
    try {
      const blob = await this.api.downloadDoctorReportPdf(report.id);
      this.viewerFile.set(new File([blob], `${periodTitle(report.periodFrom, report.periodTo)}.pdf`, { type: 'application/pdf' }));
    } catch {
      this.toast.error('Не удалось открыть PDF');
    }
  }

  private async revoke(report: DoctorReport): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Отозвать доступ?',
      message: 'Ссылка сразу перестанет работать. Врач, у которого она открыта, потеряет доступ к отчёту.',
      confirmText: 'Отозвать',
      danger: true,
    });
    if (!ok) return;
    try {
      await this.api.revokeDoctorReport(report.id);
      this.toast.success('Доступ отозван');
      await this.load();
    } catch {
      this.toast.error('Не удалось отозвать доступ');
    }
  }

  private async remove(report: DoctorReport): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Удалить отчёт?',
      message: 'PDF будет удалён без возможности восстановления, а ссылка для врача перестанет работать.',
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;
    try {
      await this.api.deleteDoctorReport(report.id);
      this.reports.update((list) => list.filter((r) => r.id !== report.id));
      this.toast.success('Отчёт удалён');
    } catch {
      this.toast.error('Не удалось удалить отчёт');
    }
  }
}
