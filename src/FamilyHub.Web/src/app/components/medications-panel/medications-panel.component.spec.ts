import { describe, expect, it } from 'vitest';
import { humanizeFieldName } from './medications-panel.component';

describe('humanizeFieldName', () => {
  it('keeps Russian names as is', () => {
    expect(humanizeFieldName('Дозировка')).toBe('Дозировка');
  });

  it('maps known English keys to Russian', () => {
    expect(humanizeFieldName('active_ingredient')).toBe('Действующее вещество');
    expect(humanizeFieldName('activeIngredient')).toBe('Действующее вещество');
    expect(humanizeFieldName('manufacturer')).toBe('Производитель');
  });

  it('makes unknown snake/camel keys readable', () => {
    expect(humanizeFieldName('shelf_life')).toBe('Shelf life');
    expect(humanizeFieldName('packSize')).toBe('Pack Size');
  });
});
