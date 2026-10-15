import { Component, OnInit, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService, ApiError } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import { AuthService } from '../../services/auth.service';
import { ToastService } from '../../shared/toast/toast.service';
import { FamilyRole, MemberStatus } from '../../models/types';
import type { HealthSummary, HomeJoinRequest, HomeMedicationAlert, HomeSummaryResponse } from '../../models/types';
import { pluralizeRu } from '../../shared/util/pluralize';
import { formatDayMonth, formatDayMonthYear } from '../../shared/util/date-format';
import { InviteModalComponent } from '../../shared/invite-modal/invite-modal.component';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { ChatRealtimeService } from '../../services/chat-realtime.service';
import { vitalsWarning } from '../../shared/util/vitals-thresholds';
import { windowToText } from '../../shared/util/vaccination-labels';
import { formatPersonName } from '../../shared/util/person-name';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { MetricCardComponent } from '../../shared/ui/metric-card/metric-card.component';
import { TaskRowComponent } from '../../shared/ui/task-row/task-row.component';
import { PersonRowComponent } from '../../shared/ui/person-row/person-row.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state/empty-state.component';
import { Tone } from '../../shared/ui/tone';
import {
  BellOff, BriefcaseMedical, Cake, CircleCheck, FlaskConical, Link, MessageCircle, Syringe, UserPlus,
  type LucideIconData,
} from 'lucide-angular';

const MONTHS_GEN = [
  'января', 'февраля', 'марта', 'апреля', 'мая', 'июня',
  'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря',
];
/** ДР попадает в карточку «Требует внимания» только в этом окне; блок «Ближайшие дни рождения»
 * справа показывает ближайшие даты вне зависимости от расстояния (см. HomeSummaryService). */
const BIRTHDAY_ATTENTION_WINDOW_DAYS = 30;
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

/** Строка «Требует внимания» (DESIGN §3 «task-row») — данные для одного <app-task-row>. Заявки на
 * вступление рисуются отдельно: у них два действия («Принять» / «Отклонить»). */
interface HomeTask {
  key: string;
  icon: LucideIconData;
  tone: Tone;
  title: string;
  subtitle: string;
  actionLabel: string;
  link: string | readonly unknown[];
  queryParams?: Record<string, string>;
}

/**
 * Главная (редизайн v2) — переписана целиком: вместо поиска (переехал в топбар каркаса, см.
 * app-search) отвечает на вопрос «что делать сейчас». Один агрегирующий запрос
 * (GET /api/home/summary) вместо 3-4 отдельных, которые раньше пришлось бы делать по лекарствам/
 * заявкам/ДР/пушу здесь на клиенте.
 */
@Component({
    selector: 'app-home',
    imports: [
      RouterLink, LoadingSpinnerComponent, InviteModalComponent, IconComponent, MetricCardComponent, TaskRowComponent,
      PersonRowComponent, EmptyStateComponent,
    ],
    templateUrl: './home.component.html',
    styleUrl: './home.component.scss'
})
export class HomeComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  readonly state = inject(FamilyStateService);
  readonly auth = inject(AuthService);
  readonly chat = inject(ChatRealtimeService);

  protected readonly icons = { CircleCheck, Link, MessageCircle, UserPlus };

  summary: HomeSummaryResponse | null = null;
  /** «Моё здоровье» — источник метрик (давление, последний анализ) и прививки «к сроку». Тот же
   * существующий запрос, что у хаба «Здоровье»; его сбой не ломает Главную — метрики просто
   * скрываются. */
  health: HealthSummary | null = null;
  loading = true;
  error: string | null = null;

  showInviteModal = false;
  /** Заявки, по которым уже идёт запрос — двойной тап «Принять» слал два запроса, второй падал. */
  private readonly busyRequests = new Set<string>();

  readonly pluralizeRu = pluralizeRu;
  readonly formatDayMonthYear = formatDayMonthYear;
  readonly formatDayMonth = formatDayMonth;
  readonly birthdayAttentionWindowDays = BIRTHDAY_ATTENTION_WINDOW_DAYS;

  /** Инвайт создаёт только админ семьи (InviteService.CreateInviteAsync) — остальным кнопки нет. */
  get canInvite(): boolean {
    const fam = this.state.selectedFamily();
    return !!fam && fam.myRole === FamilyRole.Admin && fam.myStatus === MemberStatus.Active;
  }

  /** Заявка «отправлена {дата}» — requestedAt приходит как ISO date-time, берём только дату. */
  requestSubtitle(req: HomeJoinRequest): string {
    const sent = `Заявка отправлена ${formatDayMonthYear(req.requestedAt.slice(0, 10))}`;
    return req.username ? `${sent} · @${req.username}` : sent;
  }

  ngOnInit(): void {
    void this.refresh();
  }

  async refresh(): Promise<void> {
    this.loading = true;
    try {
      const [summary, health] = await Promise.all([
        this.api.getHomeSummary(),
        this.api.getHealthSummary().catch(() => null),
      ]);
      this.summary = summary;
      this.health = health;
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить Главную.';
    } finally {
      this.loading = false;
    }
  }

  /** «{день недели}, {дата} · в семье «{X}» N уведомлений» — N берётся из unreadNotifications
   * (непрочитанные, тот же счётчик, что бейдж в боковом меню — NotificationStateService), а не
   * из числа показанных карточек «Требует внимания»: та цифра считает совсем другую вещь
   * (сколько ТЕМ требуют разбора), и раньше в этой строке путалась с числом уведомлений
   * (см. заметку редизайна v2.1 «3 дела → 3 уведомления»). Число видимых карточек по-прежнему
   * доступно отдельно — см. visibleCardCount, используется у заголовка «Требует внимания». */
  get dateLine(): string {
    if (!this.summary) return '';
    const d = new Date(this.summary.today);
    const weekday = WEEKDAYS[d.getUTCDay()];
    const dateText = `${d.getUTCDate()} ${MONTHS_GEN[d.getUTCMonth()]}`;
    const capitalized = weekday.charAt(0).toUpperCase() + weekday.slice(1);
    // Счётчик непрочитанного — на колокольчике в шапке, здесь не дублируется (макет home-desktop).
    const familyPart = this.summary.primaryFamilyName ? ` · семья «${this.summary.primaryFamilyName}»` : '';
    return `${capitalized}, ${dateText}${familyPart}`;
  }

  get visibleCardCount(): number {
    if (!this.summary) return 0;
    return this.tasks.length + this.summary.joinRequests.length;
  }

  /** «Требует внимания» — по одной строке на тему, самое срочное первым (первая строка — inverse). */
  get tasks(): HomeTask[] {
    const s = this.summary;
    if (!s) return [];
    const list: HomeTask[] = [];
    const med = this.medicationSummary;
    if (med) {
      list.push({
        key: 'meds', icon: BriefcaseMedical, tone: med.severity === 'expired' ? 'danger' : 'warning',
        title: med.title, subtitle: med.subtitle, actionLabel: 'Разобрать',
        link: ['/health/medications'], queryParams: { familyId: med.first.familyId, medkitId: med.first.medkitId },
      });
    }
    const latest = this.health?.analyses?.latest;
    if (latest && latest.abnormalIndicatorCount > 0) {
      const n = latest.abnormalIndicatorCount;
      list.push({
        key: 'analysis', icon: FlaskConical, tone: 'danger',
        title: `${n} ${pluralizeRu(n, 'показатель', 'показателя', 'показателей')} вне нормы`,
        subtitle: [latest.title, formatDayMonth(latest.recordDate)].filter(Boolean).join(' · '),
        actionLabel: 'Открыть', link: ['/health/records', latest.id],
      });
    }
    const due = this.health?.vaccinations?.nextDue;
    if (due) {
      const until = windowToText(due.windowTo);
      list.push({
        key: 'vaccination', icon: Syringe, tone: 'warning',
        title: `Прививка: ${due.seriesName}`,
        subtitle: until ? `Рекомендуется ${until}` : 'Рекомендуется сейчас',
        actionLabel: 'Отметить', link: this.vaccinationHref(),
      });
    }
    const birthday = this.birthdaySummary;
    if (birthday) {
      list.push({
        key: 'birthday', icon: Cake, tone: 'warm', title: birthday.title, subtitle: birthday.subtitle,
        actionLabel: 'Все дни рождения', link: this.birthdaysHref(), queryParams: { tab: 'birthdays' },
      });
    }
    if (s.ok.medicationsTotal === 0) {
      list.push({
        key: 'medkit-empty', icon: BriefcaseMedical, tone: 'neutral', title: 'Аптечка пуста',
        subtitle: 'Добавьте лекарства — напомним, когда истечёт срок', actionLabel: 'Добавить',
        link: ['/health/medications'],
      });
    }
    if (!s.ok.pushEnabled) {
      list.push({
        key: 'push', icon: BellOff, tone: 'neutral', title: 'Уведомления выключены',
        subtitle: 'Без них не придут напоминания о приёме и сроках', actionLabel: 'Включить',
        link: ['/settings/notifications'],
      });
    }
    return list;
  }

  /** Метрика «Давление» — последний замер из дневника; бейдж только когда выходит за ориентир. */
  get pressure(): { value: string; warning: string | null; caption: string } | null {
    const bp = this.health?.diary?.latestBloodPressure;
    if (!bp) return null;
    const warning = vitalsWarning('blood_pressure', bp.value, bp.value2);
    return {
      value: bp.value2 != null ? `${bp.value}/${bp.value2}` : String(bp.value),
      // Короткая формулировка для бейджа — «выше рекомендуемого» (DESIGN §2.1: warning).
      warning: warning ? warning.split(' (')[0] : null,
      caption: formatDayMonth(bp.occurredAt.slice(0, 10)),
    };
  }

  get latestAnalysis() {
    return this.health?.analyses?.latest ?? null;
  }

  memberName(m: { firstName: string | null; lastName: string | null; middleName: string | null }): string {
    return `${m.firstName ?? ''} ${m.lastName ?? ''}`.trim() || formatPersonName(m, 'full');
  }

  memberRole(m: { id: string; role: number }): string {
    if (this.isPending(m.id)) return 'Ждёт подтверждения';
    const admin = m.role === FamilyRole.Admin;
    if (m.id === this.auth.me()?.userId) return admin ? 'Вы · админ' : 'Вы';
    return admin ? 'Админ' : 'Участник';
  }

  familyUnread(familyId: string): number {
    return this.chat.unreadByFamily()[familyId] ?? 0;
  }

  /** Дни рождения живут во вкладке семьи — туда, а не на дублирующий /birthdays (аудит §2). */
  birthdaysHref(): string[] {
    const fam = this.state.selectedFamily();
    return fam ? ['/families', fam.id] : ['/birthdays'];
  }

  private vaccinationHref(): string[] {
    const id = this.auth.me()?.userId;
    return id ? ['/health/vaccinations/people/user', id] : ['/health/vaccinations'];
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

    const dateText = formatDayMonthYear(first.expiryDate);
    const subtitle = `Аптечка «${first.medkitName}» · ${first.name} ${first.severity === 'expired' ? `просрочен с ${dateText}` : `истекает ${dateText}`}`;

    return { title, subtitle, severity: expired.length > 0 ? 'expired' : 'expiring', first };
  }

  get birthdaySummary(): BirthdayAttentionSummary | null {
    // Бэк отдаёт три ближайшие даты вне зависимости от расстояния — в карточку внимания идут
    // только те, что попали в окно.
    const items = (this.summary?.birthdays ?? []).filter((b) => b.daysUntil <= BIRTHDAY_ATTENTION_WINDOW_DAYS);
    if (items.length === 0) return null;

    // Имя — именительный падеж (как формат ФИО с бэка и отдаёт), поэтому фраза построена так,
    // чтобы не требовать склонения ("Валерии", "Артёма" и т.п.) — общее решение недостижимо без
    // морфологического анализатора, которого в проекте нет и не планируется ради одной фразы.
    // На ближайшую дату может выпасть несколько человек — перечисляем всех.
    const nearestDays = items[0].daysUntil;
    const nearest = items.filter((b) => b.daysUntil === nearestDays);
    const next = items.find((b) => b.daysUntil > nearestDays);
    const names = nearest.map((b) => b.personName).join(', ');
    const daysWord = pluralizeRu(nearestDays, 'день', 'дня', 'дней');

    let title: string;
    if (nearest.length === 1) {
      const turningAge = nearest[0].turningAge;
      title = nearestDays === 0
        ? `Сегодня день рождения — ${names}, ${this.ageLabel(turningAge)}`
        : `${names} — день рождения через ${nearestDays} ${daysWord}, исполнится ${turningAge}`;
    } else {
      title = nearestDays === 0
        ? `Сегодня дни рождения — ${names}`
        : `Дни рождения через ${nearestDays} ${daysWord} — ${names}`;
    }

    const nextNames = next
      ? items.filter((b) => b.daysUntil === next.daysUntil).map((b) => b.personName).join(', ')
      : '';
    const subtitle = next
      ? `${formatDayMonth(nearest[0].date)}. Дальше — ${nextNames}, ${formatDayMonth(next.date)}`
      : formatDayMonth(nearest[0].date);

    return { title, subtitle };
  }

  isBusy(req: HomeJoinRequest): boolean {
    return this.busyRequests.has(req.userId + req.familyId);
  }

  ageLabel(age: number): string {
    return `${age} ${pluralizeRu(age, 'год', 'года', 'лет')}`;
  }

  async approve(req: HomeJoinRequest): Promise<void> {
    if (this.isBusy(req)) return;
    this.busyRequests.add(req.userId + req.familyId);
    try {
      await this.api.approveMember(req.familyId, req.userId);
      await Promise.all([this.refresh(), this.state.refresh()]);
      this.toast.success('Заявка принята.');
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Ошибка при подтверждении.');
    } finally {
      this.busyRequests.delete(req.userId + req.familyId);
    }
  }

  async reject(req: HomeJoinRequest): Promise<void> {
    if (this.isBusy(req)) return;
    this.busyRequests.add(req.userId + req.familyId);
    try {
      await this.api.rejectMember(req.familyId, req.userId);
      await this.refresh();
      this.toast.success('Заявка отклонена.');
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Ошибка при отклонении.');
    } finally {
      this.busyRequests.delete(req.userId + req.familyId);
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
