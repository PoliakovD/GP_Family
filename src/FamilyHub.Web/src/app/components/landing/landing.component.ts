import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  BookOpen, BriefcaseMedical, Camera, FlaskConical, Heart, Layers, LockKeyhole, MapPin, PawPrint, Search, Send,
  ShieldCheck, Stethoscope, Syringe, UserCheck, Users,
} from 'lucide';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { IconTileComponent } from '../../shared/ui/icon-tile/icon-tile.component';
import { TaskRowComponent } from '../../shared/ui/task-row/task-row.component';
import { BirthdayRowComponent } from '../../shared/ui/birthday-row/birthday-row.component';
import { LandingDemoComponent, LandingDemoStep } from './landing-demo.component';
import { LandingMockComponent } from './landing-mock.component';
import { LandingReportPreviewComponent } from './landing-report-preview.component';
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
 * Три главные фичи — демо по прокрутке (LandingDemoComponent) с макетами экранов (LandingMockComponent).
 */
@Component({
  selector: 'app-landing',
  standalone: true,
  imports: [RouterLink, IconComponent, IconTileComponent, TaskRowComponent, BirthdayRowComponent, LandingDemoComponent, LandingMockComponent, LandingReportPreviewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './landing.component.html',
  styleUrl: './landing.component.scss',
})
export class LandingComponent {
  protected readonly icons = { Heart, LockKeyhole, ShieldCheck, UserCheck };

  protected readonly year = new Date().getFullYear();

  /** «Требует внимания» в формате Главной: строки близких подписаны именем (HomeComponent). */
  protected readonly attention = [
    { icon: BriefcaseMedical, tone: 'danger' as Tone, title: 'Просрочено 1 лекарство, ещё 1 истекает',
      subtitle: 'Аптечка «Дом» · Нурофен просрочен с 1 октября 2026', action: 'Разобрать' },
    { icon: FlaskConical, tone: 'danger' as Tone, title: '2 показателя вне нормы',
      subtitle: 'Мама · Общий анализ крови · 8 октября', action: 'Открыть' },
    { icon: Syringe, tone: 'warning' as Tone, title: 'Прививка: Грипп', subtitle: 'Папа · Рекомендуется до 30 ноября 2026', action: 'Отметить' },
    { icon: Syringe, tone: 'warning' as Tone, title: 'Прививка: Корь', subtitle: 'Маша · Рекомендуется сейчас', action: 'Отметить' },
  ];

  protected readonly birthdays = [
    { day: 24, month: 'окт', name: 'Бабушка Валя', subtitle: 'Исполнится 70' },
    { day: 3, month: 'ноя', name: 'Маша', subtitle: 'Исполнится 8' },
    { day: 15, month: 'дек', name: 'Папа', subtitle: 'Исполнится 62' },
  ];

  protected readonly features: readonly Feature[] = [
    { icon: BriefcaseMedical, tone: 'neutral', title: 'Домашняя аптечка',
      text: 'Что есть дома и сроки годности. При приёме по курсу остаток можно списывать автоматически.' },
    { icon: Send, tone: 'neutral', title: 'Telegram',
      text: 'Вход через бота или Mini App, напоминания и уведомления — прямо в Telegram.' },
    { icon: Layers, tone: 'neutral', title: 'Несколько документов сразу',
      text: 'До 10 файлов за раз — каждый станет отдельной записью, анализ или приём врача определится сам.' },
    { icon: Stethoscope, tone: 'neutral', title: 'Заключение врача → курс',
      text: 'ИИ распознаёт заключение и назначения — курс приёма лекарств создаётся кнопкой «Начать курс».' },
    { icon: Camera, tone: 'neutral', title: 'Прививки по фото',
      text: 'Сфотографируйте сертификат прививок — записи распознаются, останется проверить и сохранить.' },
    { icon: PawPrint, tone: 'warm', title: 'Питомцы',
      text: 'Анализы и приёмы для питомцев — и отчёт для ветеринара в том же формате.' },
    { icon: Search, tone: 'neutral', title: 'Поиск по всему',
      text: 'Лекарство, анализ, врач или человек — одна строка поиска по всему, что есть в семье.' },
    { icon: BookOpen, tone: 'neutral', title: 'Справочник',
      text: 'Препараты, показатели анализов и прививки — простыми словами, с нормами и противопоказаниями.' },
    { icon: Users, tone: 'warm', title: 'Семья',
      text: 'Чат семьи, дни рождения, близкие и питомцы — каждый видит только то, что ему открыли.' },
  ];

  protected readonly reportPoints = [
    'Данные пациента, период и пометка, что отчёт составлен в приложении, — в шапке.',
    'Содержание и нумерованные блоки: жалобы, препараты, динамика анализов, домашние замеры, приёмы врача, прививки.',
    'В динамике — значения по датам и стрелки ↑ ↓ у отклонений от референса.',
    'PDF или ссылка без регистрации — сама перестанет работать в выбранный срок.',
  ];

  protected readonly aiSteps: readonly LandingDemoStep[] = [
    { title: 'Загрузите фото или PDF бланка', text: 'Снимите бланк телефоном или выберите файл — подойдут и несколько страниц.' },
    { title: 'ИИ распознаёт показатели', text: 'Название, дата, значения и нормы заполняются сами за 1–3 минуты. Можно закрыть приложение — пришлём уведомление.' },
    { title: 'Видно, что вне нормы', text: 'Отклонения подсвечены прямо в записи, а «Резюме для врача» простыми словами подскажет, что обсудить на приёме.' },
  ];

  protected readonly healthSteps: readonly LandingDemoStep[] = [
    { title: 'Лекарства по расписанию', text: 'Напоминание в нужное время и отметка «принято» в одно касание — видно, что пропущено.' },
    { title: 'Дневник самочувствия', text: 'Давление, пульс, симптомы, сон. FamilyHub отметит, если давление выше рекомендуемого.' },
    { title: 'Медкарта — и близких тоже', text: 'Анализы, приёмы врача, показатели и прививки. В разделах — переключатель «Все / Я / Мама»: данные близких, если они открыли вам доступ.' },
  ];

  protected readonly reportSteps: readonly LandingDemoStep[] = [
    { title: 'Выберите период', text: 'Месяц, полгода или свои даты — сразу видно, сколько данных попадёт в отчёт.' },
    { title: 'Отметьте, что показать', text: 'Динамика анализов, ИИ-резюме, препараты, замеры из дневника, приёмы врача и прививки — только то, что нужно этому врачу.' },
    { title: 'Отправьте врачу', text: 'PDF в один клик или ссылка, которая сама перестанет работать в выбранный срок.' },
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
