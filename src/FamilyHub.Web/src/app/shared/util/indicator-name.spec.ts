import { describe, expect, it } from 'vitest';
import { indicatorShortName } from './indicator-name';

describe('indicatorShortName', () => {
  it('keeps the Latin abbreviation from the blank instead of the folded Cyrillic key', () => {
    expect(indicatorShortName('MCH (среднее содержание Hb в эритроците)', 'мч')).toBe('MCH');
    expect(indicatorShortName('MCV', 'мкв')).toBe('MCV');
    expect(indicatorShortName('RDW-CV', 'рдв')).toBe('RDW-CV');
    expect(indicatorShortName('MCHC (средняя концентрация Hb в эритроците)', 'мсnс')).toBe('MCHC');
  });

  it('strips explanations in brackets and units, keeping case and script', () => {
    expect(indicatorShortName('Гемоглобин (HGB), г/л', 'гемоглобин')).toBe('Гемоглобин');
    expect(indicatorShortName('Лейкоциты, ×10^9/л', 'лейкоциты')).toBe('Лейкоциты');
    expect(indicatorShortName('Лейкоциты ×10^9/л', 'лейкоциты')).toBe('Лейкоциты');
    expect(indicatorShortName('Креатинин, мкмоль/л', 'креатинин')).toBe('Креатинин');
    expect(indicatorShortName('СОЭ, мм/ч', 'соэ')).toBe('СОЭ');
    expect(indicatorShortName('Нейтрофилы, %', 'нейтрофилы')).toBe('Нейтрофилы');
    expect(indicatorShortName('СРБ', 'срб')).toBe('СРБ');
  });

  it('keeps meaningful digits and hyphens inside the name', () => {
    expect(indicatorShortName('Витамин B12', 'витамин b12')).toBe('Витамин B12');
    expect(indicatorShortName('17-ОН-прогестерон', '17 он прогестерон')).toBe('17-ОН-прогестерон');
  });

  it('falls back to the full name when nothing is left after stripping', () => {
    expect(indicatorShortName('(HGB)', 'hgb')).toBe('(HGB)');
  });

  it('falls back to the capitalized key only when there is no display name', () => {
    expect(indicatorShortName('', 'гемоглобин')).toBe('Гемоглобин');
    expect(indicatorShortName(null, 'гемоглобин')).toBe('Гемоглобин');
    expect(indicatorShortName(undefined, '')).toBe('');
  });
});
