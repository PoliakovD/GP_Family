import { Component, OnInit, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, ApiService } from '../../services/api.service';
import { VaccinationStateService } from '../../services/vaccination-state.service';
import { ToastService } from '../../shared/toast/toast.service';
import { AvatarComponent } from '../../shared/avatar/avatar.component';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import {
  VaccinationAttentionCard, VaccinationDatePrecision, VaccinationKind, VaccinationOverview,
  VaccinationStatus, VaccinationSubject,
} from '../../models/types';
import { statusColor, statusIcon, windowToText } from '../../shared/util/vaccination-labels';
import { todayLocal } from '../../shared/util/intake-labels';

/**
 * Обзор «Прививки» (макет «Screen - Vaccination»): «Стоит запланировать» (карточки по всем людям,
 * у кого срок скоро или уже прошёл) и «Семья» (полоса done/скоро/можно/нет данных на человека).
 */
@Component({
  selector: 'app-vaccinations-overview',
  imports: [AvatarComponent, LoadingSpinnerComponent, RouterLink],
  templateUrl: './vaccinations-overview.component.html',
  styleUrl: './vaccinations-overview.component.scss',
})
export class VaccinationsOverviewComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  protected readonly vaccinations = inject(VaccinationStateService);

  protected readonly Status = VaccinationStatus;
  protected readonly statusIcon = statusIcon;
  protected readonly statusColor = statusColor;
  protected readonly windowToText = windowToText;

  readonly overview = signal<VaccinationOverview | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly markingKey = signal<string | null>(null);

  constructor() {
    effect(() => {
      this.vaccinations.version();
      untracked(() => void this.load());
    });
  }

  ngOnInit(): void {
    void this.load();
    void this.vaccinations.ensureCatalog();
  }

  protected personLink(subject: VaccinationSubject): string[] {
    return ['/health/vaccinations/people', subject.kind, subject.id];
  }

  protected person(s: VaccinationSubject): { key: string; firstName: string } {
    return { key: s.id, firstName: s.name };
  }

  protected fraction(count: number, total: number): number {
    return total > 0 ? (count / total) * 100 : 0;
  }

  protected cardKey(card: VaccinationAttentionCard): string {
    return `${card.subject.kind}:${card.subject.id}:${card.item.seriesCode}:${card.item.doseIndex}`;
  }

  protected isBusy(card: VaccinationAttentionCard): boolean {
    return this.markingKey() === this.cardKey(card);
  }

  protected async quickMarkDone(card: VaccinationAttentionCard): Promise<void> {
    const key = this.cardKey(card);
    this.markingKey.set(key);
    try {
      const item = await this.api.createVaccination({
        subjectKind: card.subject.kind,
        subjectId: card.subject.id,
        seriesCode: card.item.seriesCode,
        doseIndex: card.item.doseIndex,
        customName: null,
        vaccineName: null,
        kind: VaccinationKind.Done,
        date: todayLocal(),
        datePrecision: VaccinationDatePrecision.Day,
        certificateId: null,
        requestWellbeingCheck: false,
      });
      this.vaccinations.showSaved({
        subject: card.subject, item, reactionHint: this.vaccinations.reactionHintFor(card.item.seriesCode), quickMark: true,
      });
      this.vaccinations.changed();
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось сохранить прививку.');
    } finally {
      this.markingKey.set(null);
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.overview.set(await this.api.getVaccinationsOverview());
      this.error.set(null);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Не удалось загрузить прививки.');
    } finally {
      this.loading.set(false);
    }
  }
}
