import { describe, expect, it } from 'vitest';
import { indicatorLabel } from './indicator-name';

describe('indicatorLabel', () => {
  it('shows the full name from the blank, not the folded Cyrillic key', () => {
    expect(indicatorLabel('MCH (среднее содержание Hb в эритроците)', 'мч')).toBe('MCH (среднее содержание Hb в эритроците)');
    expect(indicatorLabel('MCV (ср. объем эритр.)', 'мкв')).toBe('MCV (ср. объем эритр.)');
    expect(indicatorLabel('RDW-CV', 'рдв')).toBe('RDW-CV');
    expect(indicatorLabel('Нейтрофилы (общ.число), %', 'нейтрофилы общ число')).toBe('Нейтрофилы (общ.число), %');
  });

  it('keeps the case and script of the name as is', () => {
    expect(indicatorLabel('СРБ', 'срб')).toBe('СРБ');
    expect(indicatorLabel('Витамин B12', 'витамин b12')).toBe('Витамин B12');
  });

  it('trims surrounding whitespace only', () => {
    expect(indicatorLabel('  Гемоглобин (HGB), г/л  ', 'гемоглобин')).toBe('Гемоглобин (HGB), г/л');
  });

  it('falls back to the capitalized key only when there is no display name', () => {
    expect(indicatorLabel('', 'гемоглобин')).toBe('Гемоглобин');
    expect(indicatorLabel(null, 'гемоглобин')).toBe('Гемоглобин');
    expect(indicatorLabel(undefined, '')).toBe('');
  });
});
