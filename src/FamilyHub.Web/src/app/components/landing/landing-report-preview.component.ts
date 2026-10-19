import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * Мини-версия PDF «Отчёт для врача» на лендинге (DESIGN §4 «Лендинг»): та же структура, что собирает
 * DoctorReportHtmlRenderer — шапка с данными пациента, пометка «не официальный документ», содержание и
 * нумерованные блоки (жалобы, препараты, динамика анализов со стрелками, домашние замеры). Иллюстрация:
 * aria-hidden, смысл — в подписи рядом. Данные вымышленные.
 */
@Component({
  selector: 'app-landing-report-preview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './landing-report-preview.component.html',
  styleUrl: './landing-report-preview.component.scss',
})
export class LandingReportPreviewComponent {
  protected readonly labs = [
    { name: 'Холестерин общий', unit: 'ммоль/л', a: '5,4', b: '6,1', flag: 'hi', ref: '3,0–5,2' },
    { name: 'ЛПНП', unit: 'ммоль/л', a: '3,1', b: '3,9', flag: 'hi', ref: '< 3,0' },
    { name: 'Глюкоза', unit: 'ммоль/л', a: '5,0', b: '5,2', flag: '', ref: '3,9–6,1' },
    { name: 'Гемоглобин', unit: 'г/л', a: '118', b: '114', flag: 'lo', ref: '120–150' },
  ];

  protected readonly measurements = [
    { name: 'Давление', count: 24, range: '118/76 – 142/92', avg: '131/86' },
    { name: 'Пульс', count: 20, range: '62 – 88', avg: '74' },
  ];
}
