import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import {
  Activity, ChartLine, ChevronLeft, Download, FileText, FlaskConical, HeartPulse, Moon, NotebookPen, Pill, Share2, Smile,
  Sparkles, Stethoscope, Syringe, Upload, Zap,
} from 'lucide';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { IconTileComponent } from '../../shared/ui/icon-tile/icon-tile.component';
import { Tone } from '../../shared/ui/tone';
import { TrendLineComponent } from '../../shared/trend-line/trend-line.component';

/**
 * Макеты экранов для демо лендинга (LandingDemoComponent): ИИ-расшифровка анализа, страница «Здоровье»,
 * отчёт для врача. Состояние — шаг демо и «нажатие уже случилось» (done). Компактнее приложения (экран
 * 324px) и упрощённо, но только то, что есть в продукте: те же подписи, плашки, кнопки и переключатели.
 * Данные вымышленные. Отдельный компонент — чтобы стили
 * макетов не раздували стили самого лендинга (бюджет anyComponentStyle).
 */
@Component({
  selector: 'app-landing-mock',
  standalone: true,
  imports: [IconComponent, IconTileComponent, TrendLineComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './landing-mock.component.html',
  styleUrl: './landing-mock.component.scss',
})
export class LandingMockComponent {
  readonly kind = input.required<'ai' | 'health' | 'report'>();
  readonly step = input(0);
  readonly done = input(false);

  protected readonly icons = {
    ChevronLeft, Upload, FileText, Sparkles, Pill, NotebookPen, Share2, Download, Zap, FlaskConical,
  };

  /** pos — место значения на шкале нормы (0–100, норма — 25–75); out — плашка отклонения, как в записи. */
  protected readonly aiRows = [
    { name: 'Гемоглобин', value: '128', unit: 'г/л', pos: 52, out: '' },
    { name: 'СОЭ', value: '24', unit: 'мм/ч', pos: 92, out: '↑ выше на 4' },
    { name: 'Лейкоциты', value: '3,6', unit: '×10⁹/л', pos: 12, out: '↓ ниже на 0,4' },
    { name: 'Тромбоциты', value: '245', unit: '×10⁹/л', pos: 46, out: '' },
    { name: 'Эритроциты', value: '4,5', unit: '×10¹²/л', pos: 58, out: '' },
  ];

  protected readonly bpTrend = [121, 118, 124, 119, 122, 117, 118, 135];

  /** Плитки «Медкарта» страницы «Моё здоровье». */
  protected readonly medcard = [
    { icon: FlaskConical, title: 'Анализы', text: 'Общий анализ крови · 3 окт', badge: '2 вне нормы', badgeTone: 'danger' },
    { icon: Stethoscope, title: 'Приёмы врача', text: 'Терапевт · 18 сентября', badge: '', badgeTone: '' },
    { icon: ChartLine, title: 'Показатели', text: 'Лейкоциты 3,6', badge: '', badgeTone: '' },
    { icon: Syringe, title: 'Прививки', text: 'Грипп · до 30 ноября', badge: 'Можно сделать', badgeTone: 'neutral' },
  ];

  /** Раздел «Анализы» с переключателем людей: свои и близкого, открывшего доступ. */
  protected readonly recordsByPerson = [
    { person: 'Иванов И.', records: [
      { title: 'Общий анализ крови', sub: '3 октября 2026 · 20 показателей', badge: '2 вне нормы', badgeTone: 'danger' },
      { title: 'Биохимия крови', sub: '12 августа 2026 · 9 показателей', badge: 'в норме', badgeTone: 'success' },
    ] },
    { person: 'Мама', records: [
      { title: 'Липидный профиль', sub: '8 октября 2026 · 6 показателей', badge: '1 вне нормы', badgeTone: 'danger' },
    ] },
  ];

  protected readonly diaryEntries = [
    { icon: Activity, tone: 'measure' as Tone, title: 'Давление 135/90', sub: '08:10 · мм рт. ст.' },
    { icon: Smile, tone: 'mood' as Tone, title: 'Самочувствие: хорошо', sub: '08:05' },
    { icon: Moon, tone: 'sleep' as Tone, title: 'Сон 7 ч 30 мин', sub: '07:40 · качество: среднее' },
  ];

  /** Короткий список настоящих переключателей формы отчёта. */
  protected readonly reportOptions = [
    { icon: ChartLine, title: 'Динамика анализов', on: true },
    { icon: Sparkles, title: 'ИИ-резюме анализов', on: false },
    { icon: Pill, title: 'Текущие препараты', on: true },
    { icon: HeartPulse, title: 'Домашние замеры, самочувствие и сон', on: true },
  ];
}
