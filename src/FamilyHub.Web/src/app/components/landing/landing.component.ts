import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  BookOpen, BriefcaseMedical, CalendarClock, Check, ChevronLeft, Download, FileText, FlaskConical, Heart, ChartLine,
  LockKeyhole, MapPin, NotebookPen, Pill, Share2, ShieldCheck, Sparkles, Stethoscope, Syringe, Upload, UserCheck, Users,
} from 'lucide';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { IconTileComponent } from '../../shared/ui/icon-tile/icon-tile.component';
import { TaskRowComponent } from '../../shared/ui/task-row/task-row.component';
import { BirthdayRowComponent } from '../../shared/ui/birthday-row/birthday-row.component';
import { TrendLineComponent } from '../../shared/trend-line/trend-line.component';
import { LandingDemoComponent, LandingDemoStep } from './landing-demo.component';
import { Tone } from '../../shared/ui/tone';

interface Feature {
  icon: typeof Heart;
  tone: Tone;
  title: string;
  text: string;
}

/**
 * Лендинг для гостя (DESIGN §4 «Лендинг»): что такое FamilyHub, что в нём есть, как начать и как
 * защищены данные. Открывается на «/» только без сессии (landingGuard) — вошедший сразу попадает на
 * Главную, в Telegram Mini App лендинга нет. Превью «Требует внимания» — декоративное (aria-hidden, inert).
 * Три главные фичи — демо по прокрутке (LandingDemoComponent): данные макетов ниже, вымышленные.
 */
@Component({
  selector: 'app-landing',
  standalone: true,
  imports: [RouterLink, IconComponent, IconTileComponent, TaskRowComponent, BirthdayRowComponent, TrendLineComponent, LandingDemoComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './landing.component.html',
  styleUrl: './landing.component.scss',
})
export class LandingComponent {
  protected readonly icons = {
    Heart, LockKeyhole, ShieldCheck, UserCheck, Pill, Syringe, CalendarClock, FlaskConical, ChevronLeft, Upload,
    FileText, Sparkles, Check, NotebookPen, Share2, Download,
  };

  protected readonly year = new Date().getFullYear();

  protected readonly features: readonly Feature[] = [
    { icon: BriefcaseMedical, tone: 'neutral', title: 'Домашняя аптечка',
      text: 'Что есть дома и у кого: сроки годности под контролем, остаток списывается при приёме.' },
    { icon: BookOpen, tone: 'neutral', title: 'Справочник',
      text: 'Препараты, показатели анализов и прививки — простыми словами, с нормами и противопоказаниями.' },
    { icon: Users, tone: 'warm', title: 'Семья',
      text: 'Чат семьи, дни рождения, близкие и питомцы — каждый видит только то, что ему открыли.' },
  ];

  protected readonly aiSteps: readonly LandingDemoStep[] = [
    { title: 'Загрузите фото или PDF бланка', text: 'Снимите бланк телефоном или выберите файл — подойдут и несколько страниц.' },
    { title: 'ИИ распознаёт показатели', text: 'Название, дата, значения и нормы заполняются сами за 1–3 минуты. Можно закрыть приложение — пришлём уведомление.' },
    { title: 'Сразу видно, что вне нормы', text: 'Отклонения подсвечены, а резюме простыми словами подскажет, что обсудить с врачом.' },
  ];

  /** pos — место значения на шкале нормы (0–100, норма — 25–75). */
  protected readonly aiRows = [
    { name: 'Гемоглобин', value: '128', unit: 'г/л', pos: 52, out: '' },
    { name: 'СОЭ', value: '24', unit: 'мм/ч', pos: 92, out: 'выше нормы' },
    { name: 'Лейкоциты', value: '3,6', unit: '×10⁹/л', pos: 12, out: 'ниже нормы' },
    { name: 'Тромбоциты', value: '245', unit: '×10⁹/л', pos: 46, out: '' },
    { name: 'Эритроциты', value: '4,5', unit: '×10¹²/л', pos: 58, out: '' },
  ];

  protected readonly healthSteps: readonly LandingDemoStep[] = [
    { title: 'Лекарства по расписанию', text: 'Напоминание в нужное время и отметка «принято» в одно касание — видно, что пропущено.' },
    { title: 'Дневник самочувствия', text: 'Давление, симптомы, сон. FamilyHub подскажет, если значение выше рекомендуемого.' },
    { title: 'Медкарта каждого в семье', text: 'Анализы, приёмы врача и прививки близких — одним переключателем, если они открыли вам доступ.' },
  ];

  protected readonly bpTrend = [121, 118, 124, 119, 122, 117, 118];
  protected readonly bpTrendNew = [121, 118, 124, 119, 122, 117, 118, 135];

  protected readonly medcardMe = [
    { icon: FlaskConical, tone: 'accent' as Tone, title: 'Анализы', text: 'Биохимия · 12 авг', badge: 'в норме', badgeTone: 'success' },
    { icon: Stethoscope, tone: 'accent' as Tone, title: 'Приёмы врача', text: 'Терапевт · 18 сен', badge: '', badgeTone: '' },
    { icon: Syringe, tone: 'accent' as Tone, title: 'Прививки', text: 'Грипп · до 30 ноя', badge: 'скоро', badgeTone: 'warning' },
    { icon: ChartLine, tone: 'accent' as Tone, title: 'Показатели', text: 'Глюкоза 5,1', badge: '', badgeTone: '' },
  ];

  protected readonly medcardMom = [
    { icon: FlaskConical, tone: 'accent' as Tone, title: 'Анализы', text: 'ОАК · сегодня', badge: '2 вне нормы', badgeTone: 'danger' },
    { icon: Stethoscope, tone: 'accent' as Tone, title: 'Приёмы врача', text: 'Кардиолог · 2 окт', badge: '', badgeTone: '' },
    { icon: Syringe, tone: 'accent' as Tone, title: 'Прививки', text: 'По календарю', badge: 'всё сделано', badgeTone: 'success' },
    { icon: ChartLine, tone: 'accent' as Tone, title: 'Показатели', text: 'Холестерин 5,8', badge: 'выше нормы', badgeTone: 'danger' },
  ];

  protected readonly reportSteps: readonly LandingDemoStep[] = [
    { title: 'Выберите период', text: 'Месяц, полгода или свои даты — сразу видно, сколько данных попадёт в отчёт.' },
    { title: 'Отметьте, что показать', text: 'Анализы вне нормы, динамика показателей, лекарства и дневник — только то, что нужно этому врачу.' },
    { title: 'Отправьте врачу', text: 'PDF в один клик или ссылка, которая сама перестанет работать в выбранный срок.' },
  ];

  protected readonly reportOptions = [
    { icon: FlaskConical, title: 'Анализы вне нормы', on: true },
    { icon: ChartLine, title: 'Динамика показателей', on: false },
    { icon: Pill, title: 'Приём лекарств', on: true },
    { icon: NotebookPen, title: 'Дневник', on: true },
  ];

  protected readonly steps = [
    { title: 'Создайте аккаунт', text: 'Email и пароль — код подтверждения придёт на почту.' },
    { title: 'Позовите близких', text: 'Отправьте ссылку-приглашение — вы подтверждаете каждого вручную.' },
    { title: 'Добавьте первое', text: 'Анализ, лекарство или прививку — остальное FamilyHub напомнит сам.' },
  ];

  protected readonly trust = [
    { icon: LockKeyhole, title: 'Шифрование', text: 'Медицинские записи, дневник и файлы хранятся зашифрованными (AES-256).' },
    { icon: UserCheck, title: 'Доступ по вашему решению', text: 'Анализы видят только те, кому вы их открыли, — доступ можно забрать.' },
    { icon: MapPin, title: 'Данные в России', text: 'Серверы и распознавание бланков — в РФ, во внешние ИИ-сервисы данные не уходят.' },
    { icon: ShieldCheck, title: 'Без рекламы', text: 'Данные не продаются и не используются для рекламы, сторонней аналитики нет.' },
  ];

}
