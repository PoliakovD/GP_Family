import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, ApiService } from '../../services/api.service';
import { AuthService } from '../../services/auth.service';
import { IntakeStateService } from '../../services/intake-state.service';
import { ToastService } from '../../shared/toast/toast.service';
import { AvatarComponent } from '../../shared/avatar/avatar.component';
import { TrendLineComponent } from '../../shared/trend-line/trend-line.component';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { DoseAction, HealthSummary, HealthSummaryIntake, HealthSummaryNextDose, VaccinationStatus } from '../../models/types';
import { ageParts } from '../../shared/util/age';
import { pluralizeRu } from '../../shared/util/pluralize';
import { statusColor, statusLabel, windowToText } from '../../shared/util/vaccination-labels';
import { formatDayMonth, formatDayMonthYear } from '../../shared/util/date-format';
import { MONTHS_GEN } from '../../shared/util/birthday-date';

/** Статус одного квадратика ряда приёмов (макет «Screen - Health hub») — сводка знает только
 * счётчики (taken/missed/...), не список доз, поэтому ряд восстанавливается приближённо: сначала
 * принятые, затем пропущенные, затем один акцентный «следующий» и остальные нейтральные — точный
 * список смотрят на самом экране «Приём лекарств», здесь только превью. */
type DoseSquare = 'taken' | 'missed' | 'next' | 'upcoming';

/**
 * Хаб «Здоровье» (редизайн навигации, макет «Screen - Health hub») — плитки трёх групп
 * («Каждый день»/«Медкарта»/«Инструменты», те же названия, что в сайдбаре, см. app.component.ts
 * healthGroups) со сводкой СВОИХ данных на каждой (`GET /api/health/summary`, один запрос вместо
 * восьми). Внутри разделов по-прежнему можно выбрать любого человека из семьи — плитки лишь
 * предвыбирают «Я» через `?person=me` там, где раздел это умеет.
 */
@Component({
  selector: 'app-health-home',
  imports: [RouterLink, AvatarComponent, TrendLineComponent, LoadingSpinnerComponent],
  templateUrl: './health-home.component.html',
  styleUrl: './health-home.component.scss',
})
export class HealthHomeComponent implements OnInit {
  private readonly api = inject(ApiService);
  protected readonly auth = inject(AuthService);
  protected readonly intake = inject(IntakeStateService);
  private readonly toast = inject(ToastService);

  protected readonly summary = signal<HealthSummary | null>(null);
  protected readonly loading = signal(true);
  protected readonly busyDose = signal(false);

  protected readonly VaccinationStatus = VaccinationStatus;
  protected readonly statusLabel = statusLabel;
  protected readonly statusColor = statusColor;
  protected readonly windowToText = windowToText;
  protected readonly formatDayMonth = formatDayMonth;
  protected readonly formatDayMonthYear = formatDayMonthYear;
  protected readonly pluralizeRu = pluralizeRu;

  /** «Иван, 28 лет» — вторая строка шапки; возраст скрыт, если дата рождения не заполнена. */
  protected readonly ageLabel = computed<string | null>(() => {
    const birth = this.auth.me()?.birthDate;
    if (!birth) return null;
    const { years } = ageParts(birth);
    return `${years} ${pluralizeRu(years, 'год', 'года', 'лет')}`;
  });

  ngOnInit(): void {
    void this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.summary.set(await this.api.getHealthSummary());
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось загрузить сводку здоровья.');
    } finally {
      this.loading.set(false);
    }
  }

  /** «Принял» прямо с плитки — тот же вызов, что на экране «Сегодня» (intake-today.component.ts):
   * отмечаем дозу, поднимаем IntakeStateService.version() (бейджи в меню это тоже увидят) и
   * перечитываем сводку, чтобы плитка сразу показала следующую дозу. Останавливаем всплытие клика —
   * сама плитка тоже ссылка на /health/intake, кнопка не должна ещё и уводить туда. */
  protected async takeDose(dose: HealthSummaryNextDose, event: Event): Promise<void> {
    event.preventDefault();
    event.stopPropagation();
    if (this.busyDose()) return;

    this.busyDose.set(true);
    try {
      await this.api.applyDose(dose.courseId, dose.scheduledAt, DoseAction.Taken);
      this.toast.success('Приём отмечен');
      this.intake.changed();
      await this.load();
    } catch (e) {
      this.toast.error(e instanceof ApiError && e.message ? e.message : 'Не удалось отметить приём.');
    } finally {
      this.busyDose.set(false);
    }
  }

  protected doseSquares(ik: HealthSummaryIntake): DoseSquare[] {
    const squares: DoseSquare[] = [];
    for (let i = 0; i < ik.taken; i++) squares.push('taken');
    for (let i = 0; i < ik.missed; i++) squares.push('missed');
    for (let i = squares.length; i < ik.total; i++) squares.push(squares.length === ik.taken + ik.missed ? 'next' : 'upcoming');
    return squares;
  }

  /** «Сегодня»/«Вчера»/«9 июня» — начало предложения, поэтому с заглавной буквы. */
  protected dayWord(iso: string): string {
    const d = new Date(iso);
    const now = new Date();
    if (this.sameDay(d, now)) return 'Сегодня';
    const yesterday = new Date(now);
    yesterday.setDate(now.getDate() - 1);
    if (this.sameDay(d, yesterday)) return 'Вчера';
    return `${d.getDate()} ${MONTHS_GEN[d.getMonth()]}`;
  }

  protected timeOfDay(iso: string): string {
    const d = new Date(iso);
    return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
  }

  private sameDay(a: Date, b: Date): boolean {
    return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  }

  /** Плитка «Прививки» ведёт сразу на график себя, а не на обзор семьи — «person=me» здесь
   * означает конкретный маршрут (vaccinations-page/vaccination-person), не query-фильтр, как у
   * остальных плиток: у прививок нет отдельного «предвыбора человека» на обзорном экране. */
  protected vaccinationHref(): string[] {
    const id = this.auth.me()?.userId;
    return id ? ['/health/vaccinations/people/user', id] : ['/health/vaccinations'];
  }
}
