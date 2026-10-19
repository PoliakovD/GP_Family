import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import {
  ChartLine, Check, ChevronLeft, Download, FileText, FlaskConical, NotebookPen, Pill, Share2, Sparkles, Stethoscope,
  Syringe, Upload,
} from 'lucide';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { IconTileComponent } from '../../shared/ui/icon-tile/icon-tile.component';
import { Tone } from '../../shared/ui/tone';
import { TrendLineComponent } from '../../shared/trend-line/trend-line.component';

/**
 * Макеты экранов для демо лендинга (LandingDemoComponent): ИИ-расшифровка анализа, страница «Здоровье»,
 * отчёт для врача. Состояние — шаг демо и «нажатие уже случилось» (done). Компактнее приложения (экран
 * 324px), но из тех же токенов и классов. Данные вымышленные. Отдельный компонент — чтобы стили
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
    ChevronLeft, Upload, FileText, Sparkles, Check, Pill, NotebookPen, Share2, Download,
  };

  /** pos — место значения на шкале нормы (0–100, норма — 25–75). */
  protected readonly aiRows = [
    { name: 'Гемоглобин', value: '128', unit: 'г/л', pos: 52, out: '' },
    { name: 'СОЭ', value: '24', unit: 'мм/ч', pos: 92, out: 'выше нормы' },
    { name: 'Лейкоциты', value: '3,6', unit: '×10⁹/л', pos: 12, out: 'ниже нормы' },
    { name: 'Тромбоциты', value: '245', unit: '×10⁹/л', pos: 46, out: '' },
    { name: 'Эритроциты', value: '4,5', unit: '×10¹²/л', pos: 58, out: '' },
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

  protected readonly reportOptions = [
    { icon: FlaskConical, title: 'Анализы вне нормы', on: true },
    { icon: ChartLine, title: 'Динамика показателей', on: false },
    { icon: Pill, title: 'Приём лекарств', on: true },
    { icon: NotebookPen, title: 'Дневник', on: true },
  ];
}
