import { Component, OnInit, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiService, ApiError } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import { AuthService } from '../../services/auth.service';
import { ToastService } from '../../shared/toast/toast.service';
import type { HomeBirthdayItem, HomeJoinRequest, HomeMedicationAlert, HomeSummaryResponse } from '../../models/types';
import { pluralizeRu } from '../../shared/util/pluralize';
import { AttentionCardComponent } from '../../shared/attention-card/attention-card.component';
import { AvatarComponent } from '../../shared/avatar/avatar.component';
import { PersonNameComponent } from '../../shared/person-name/person-name.component';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';

const MONTHS_GEN = [
  'января', 'февраля', 'марта', 'апреля', 'мая', 'июня',
  'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря',
];
const MONTHS_SHORT = ['янв', 'фев', 'мар', 'апр', 'мая', 'июн', 'июл', 'авг', 'сен', 'окт', 'ноя', 'дек'];
const WEEKDAYS = ['воскресенье', 'понедельник', 'вторник', 'среда', 'четверг', 'пятница', 'суббота'];

/** Аггрегированная карточка «Требует внимания» по лекарствам — редизайн v2. Одна карточка на
 * ВСЕ просроченные/истекающие лекарства сразу (не по одной на каждое, как в сыром списке с
 * бэка) — тот же приём, что показывает дизайн-макет: "Просрочено 1, ещё 2 истекают". */
interface MedicationAttentionSummary {
  title: string;
  subtitle: string;
  severity: 'expired' | 'expiring';
  first: HomeMedicationAlert;
}

/** Аналогично для дней рождения — ближайшее выделено, следующее упомянуто одной строкой. */
interface BirthdayAttentionSummary {
  title: string;
  subtitle: string;
}

/**
 * Главная (редизайн v2) — переписана целиком: вместо поиска (переехал в топбар каркаса, см.
 * app-search) отвечает на вопрос «что делать сейчас». Один агрегирующий запрос
 * (GET /api/home/summary) вместо 3-4 отдельных, которые раньше пришлось бы делать по лекарствам/
 * заявкам/ДР/пушу здесь на клиенте.
 */
@Component({
  selector: 'app-home',
  standalone: true,
  imports: [RouterLink, AttentionCardComponent, AvatarComponent, PersonNameComponent, LoadingSpinnerComponent],
  templateUrl: './home.component.html',
  styleUrl: './home.component.scss',
})
export class HomeComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  readonly state = inject(FamilyStateService);
  readonly auth = inject(AuthService);

  summary: HomeSummaryResponse | null = null;
  loading = true;
  error: string | null = null;

  ngOnInit(): void {
    void this.refresh();
  }

  async refresh(): Promise<void> {
    this.loading = true;
    try {
      this.summary = await this.api.getHomeSummary();
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить Главную.';
    } finally {
      this.loading = false;
    }
  }

  /** «{день недели}, {дата} · в семье «{X}» N дел» — N считается по фактически ПОКАЗАННЫМ
   * карточкам (топикам), не по сырому количеству строк с бэка: лекарства/ДР схлопываются в одну
   * карточку каждый, поэтому "3 дела" в тексте должно совпадать с "3 карточками на экране", а не
   * с суммой отдельных лекарств+заявок+дней рождения. */
  get dateLine(): string {
    if (!this.summary) return '';
    const d = new Date(this.summary.today);
    const weekday = WEEKDAYS[d.getUTCDay()];
    const dateText = `${d.getUTCDate()} ${MONTHS_GEN[d.getUTCMonth()]}`;
    const capitalized = weekday.charAt(0).toUpperCase() + weekday.slice(1);
    const count = this.visibleCardCount;
    if (count === 0) return `${capitalized}, ${dateText}`;
    const familyPart = this.summary.primaryFamilyName ? ` · в семье «${this.summary.primaryFamilyName}»` : '';
    return `${capitalized}, ${dateText}${familyPart} ${count} ${pluralizeRu(count, 'дело', 'дела', 'дел')}`;
  }

  get visibleCardCount(): number {
    if (!this.summary) return 0;
    return (this.medicationSummary ? 1 : 0) + this.summary.joinRequests.length + (this.birthdaySummary ? 1 : 0);
  }

  get medicationSummary(): MedicationAttentionSummary | null {
    const meds = this.summary?.medications ?? [];
    if (meds.length === 0) return null;

    const expired = meds.filter((m) => m.severity === 'expired');
    const expiring = meds.filter((m) => m.severity === 'expiring');
    const first = meds[0]; // уже отсортированы по ExpiryDate ASC на бэке — самое срочное первое

    let title: string;
    if (expired.length > 0 && expiring.length > 0) {
      title = `Просрочен${expired.length === 1 ? 'о' : 'ы'} ${expired.length} ${pluralizeRu(expired.length, 'лекарство', 'лекарства', 'лекарств')}, ещё ${expiring.length} истека${expiring.length === 1 ? 'ет' : 'ют'}`;
    } else if (expired.length > 0) {
      title = `Просрочен${expired.length === 1 ? 'о' : 'ы'} ${expired.length} ${pluralizeRu(expired.length, 'лекарство', 'лекарства', 'лекарств')}`;
    } else {
      title = `Истека${expiring.length === 1 ? 'ет' : 'ют'} ${expiring.length} ${pluralizeRu(expiring.length, 'лекарство', 'лекарства', 'лекарств')}`;
    }

    const subtitle = `Аптечка «${first.medkitName}» · ${first.name} ${first.severity === 'expired' ? 'просрочен' : 'истекает'}`;

    return { title, subtitle, severity: expired.length > 0 ? 'expired' : 'expiring', first };
  }

  get birthdaySummary(): BirthdayAttentionSummary | null {
    const items = this.summary?.birthdays ?? [];
    if (items.length === 0) return null;

    // Имя — именительный падеж (как формат ФИО с бэка и отдаёт), поэтому фраза построена так,
    // чтобы не требовать склонения ("Валерии", "Артёма" и т.п.) — общее решение недостижимо без
    // морфологического анализатора, которого в проекте нет и не планируется ради одной фразы.
    const [nearest, next] = items;
    const daysWord = pluralizeRu(nearest.daysUntil, 'день', 'дня', 'дней');
    const title = nearest.daysUntil === 0
      ? `Сегодня день рождения — ${nearest.personName}, ${nearest.turningAge} лет`
      : `${nearest.personName} — день рождения через ${nearest.daysUntil} ${daysWord}, исполнится ${nearest.turningAge}`;
    const subtitle = next
      ? `Дальше — ${next.personName}, через ${next.daysUntil} ${pluralizeRu(next.daysUntil, 'день', 'дня', 'дней')}`
      : '';

    return { title, subtitle };
  }

  goToMedkit(alert: HomeMedicationAlert): void {
    void this.router.navigate(['/health/medications'], {
      queryParams: { familyId: alert.familyId, medkitId: alert.medkitId },
    });
  }

  async approve(req: HomeJoinRequest): Promise<void> {
    try {
      await this.api.approveMember(req.familyId, req.userId);
      await Promise.all([this.refresh(), this.state.refresh()]);
      this.toast.success('Заявка принята.');
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Ошибка при подтверждении.');
    }
  }

  async reject(req: HomeJoinRequest): Promise<void> {
    try {
      await this.api.rejectMember(req.familyId, req.userId);
      await this.refresh();
      this.toast.success('Заявка отклонена.');
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Ошибка при отклонении.');
    }
  }

  /** Пометка «ждёт подтверждения» в «Кто в семье» — кросс-ссылка на уже загруженный список
   * заявок (не новый запрос): заявки видит только Admin, поэтому у обычного участника пометка
   * молча не появится — минорная деградация, не ошибка (см. план редизайна, PR3a). */
  /** "yyyy-MM-dd" → число дня месяца, без сдвига часовым поясом (см. dateLine). */
  dayOfMonth(dateStr: string): number {
    return new Date(dateStr).getUTCDate();
  }

  monthShort(dateStr: string): string {
    return MONTHS_SHORT[new Date(dateStr).getUTCMonth()];
  }

  isPending(memberId: string): boolean {
    const familyId = this.state.selectedFamily()?.id;
    return this.summary?.joinRequests.some((r) => r.userId === memberId && r.familyId === familyId) ?? false;
  }
}
