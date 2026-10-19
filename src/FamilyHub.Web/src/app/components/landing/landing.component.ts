import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  BookOpen, BriefcaseMedical, CalendarClock, FlaskConical, Heart, LockKeyhole, MapPin, Pill, ShieldCheck, Syringe,
  UserCheck, Users,
} from 'lucide';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { IconTileComponent } from '../../shared/ui/icon-tile/icon-tile.component';
import { TaskRowComponent } from '../../shared/ui/task-row/task-row.component';
import { BirthdayRowComponent } from '../../shared/ui/birthday-row/birthday-row.component';
import { LandingDemoComponent, LandingDemoStep } from './landing-demo.component';
import { LandingMockComponent } from './landing-mock.component';
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
  imports: [RouterLink, IconComponent, IconTileComponent, TaskRowComponent, BirthdayRowComponent, LandingDemoComponent, LandingMockComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './landing.component.html',
  styleUrl: './landing.component.scss',
})
export class LandingComponent {
  protected readonly icons = { Heart, LockKeyhole, ShieldCheck, UserCheck, Pill, Syringe, CalendarClock, FlaskConical };

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

  protected readonly healthSteps: readonly LandingDemoStep[] = [
    { title: 'Лекарства по расписанию', text: 'Напоминание в нужное время и отметка «принято» в одно касание — видно, что пропущено.' },
    { title: 'Дневник самочувствия', text: 'Давление, симптомы, сон. FamilyHub подскажет, если значение выше рекомендуемого.' },
    { title: 'Медкарта каждого в семье', text: 'Анализы, приёмы врача и прививки близких — одним переключателем, если они открыли вам доступ.' },
  ];

  protected readonly reportSteps: readonly LandingDemoStep[] = [
    { title: 'Выберите период', text: 'Месяц, полгода или свои даты — сразу видно, сколько данных попадёт в отчёт.' },
    { title: 'Отметьте, что показать', text: 'Анализы вне нормы, динамика показателей, лекарства и дневник — только то, что нужно этому врачу.' },
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
