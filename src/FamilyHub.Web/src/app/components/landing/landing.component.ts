import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  BriefcaseMedical, CalendarClock, MapPin, Cake, FlaskConical, Heart, LockKeyhole, NotebookPen,
  Pill, ShieldCheck, Syringe, UserCheck, Users,
} from 'lucide';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { IconTileComponent } from '../../shared/ui/icon-tile/icon-tile.component';
import { TaskRowComponent } from '../../shared/ui/task-row/task-row.component';
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
 */
@Component({
  selector: 'app-landing',
  standalone: true,
  imports: [RouterLink, IconComponent, IconTileComponent, TaskRowComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './landing.component.html',
  styleUrl: './landing.component.scss',
})
export class LandingComponent {
  protected readonly icons = {
    Heart, LockKeyhole, ShieldCheck, UserCheck, Pill, Syringe, CalendarClock, FlaskConical, Cake,
  };

  protected readonly year = new Date().getFullYear();

  protected readonly features: readonly Feature[] = [
    { icon: FlaskConical, tone: 'measure', title: 'Анализы и приёмы врача',
      text: 'Загрузите фото или PDF бланка — показатели распознаются сами, вне нормы видно сразу. Динамика по каждому показателю.' },
    { icon: Pill, tone: 'medication', title: 'Приём лекарств',
      text: 'Курсы по расписанию с напоминаниями, отметка «принял» в одно касание и история пропусков.' },
    { icon: BriefcaseMedical, tone: 'neutral', title: 'Домашняя аптечка',
      text: 'Что есть дома и у кого: сроки годности под контролем, остаток списывается при приёме.' },
    { icon: Syringe, tone: 'neutral', title: 'Прививки',
      text: 'Календарь прививок для детей и взрослых: что сделано, что скоро и что пропущено.' },
    { icon: NotebookPen, tone: 'note', title: 'Дневник и отчёт для врача',
      text: 'Симптомы, давление, сон и самочувствие — и PDF для врача за выбранный период за пару минут.' },
    { icon: Users, tone: 'warm', title: 'Семья',
      text: 'Чат семьи, дни рождения, близкие и питомцы — каждый видит только то, что ему открыли.' },
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
