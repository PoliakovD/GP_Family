import { describe, expect, it } from 'vitest';
import {
  confidenceLabel, diffPayload, indexAfterRemoval, isTypingTarget, payloadFieldLabel, previewValue,
  reviewKindLabel, reviewOriginLabel,
} from './review-helpers';

describe('confidenceLabel', () => {
  it('formats a 0..1 confidence as a rounded percentage', () => {
    expect(confidenceLabel(0.854)).toBe('85%');
    expect(confidenceLabel(1)).toBe('100%');
    expect(confidenceLabel(0)).toBe('0%');
  });

  it('shows missing confidence explicitly (counts as below threshold on the backend)', () => {
    expect(confidenceLabel(null)).toBe('нет оценки');
    expect(confidenceLabel(undefined)).toBe('нет оценки');
  });
});

describe('labels', () => {
  it('maps kinds and origins, falling back to the raw value for unknown ones', () => {
    expect(reviewKindLabel('lab-analyte')).toBe('Показатель');
    expect(reviewOriginLabel('manual')).toBe('Введён вручную');
    expect(reviewOriginLabel('unknown' as never)).toBe('unknown');
    expect(payloadFieldLabel('refRanges')).toBe('Референсные диапазоны');
    expect(payloadFieldLabel('someNewKey')).toBe('someNewKey');
  });
});

describe('diffPayload', () => {
  const draft = JSON.stringify({ schemaVersion: 3, form: 'таблетки', purpose: 'новое', tradeNames: ['А', 'Б'] });

  it('returns only top-level keys whose values differ from the current kb entry', () => {
    const current = JSON.stringify({ schemaVersion: 2, form: 'таблетки', purpose: 'старое', tradeNames: ['А', 'Б'] });
    const diff = diffPayload(draft, current);
    expect(diff.map((d) => d.key)).toEqual(['purpose']);
    expect(diff[0].before).toBe('старое');
    expect(diff[0].after).toBe('новое');
  });

  it('ignores object key order and the service schemaVersion field', () => {
    const current = JSON.stringify({ tradeNames: ['А', 'Б'], purpose: 'новое', form: 'таблетки', schemaVersion: 1 });
    expect(diffPayload(draft, current)).toEqual([]);
  });

  it('treats every non-empty draft key as new when there is no current entry', () => {
    const keys = diffPayload(draft, null).map((d) => d.key).sort();
    expect(keys).toEqual(['form', 'purpose', 'tradeNames']);
  });

  it('detects removed keys and array order changes', () => {
    const current = JSON.stringify({ form: 'таблетки', purpose: 'новое', tradeNames: ['Б', 'А'], storage: 'сухо' });
    expect(diffPayload(draft, current).map((d) => d.key).sort()).toEqual(['storage', 'tradeNames']);
  });

  it('tolerates invalid JSON on either side', () => {
    expect(diffPayload('not json', 'also not')).toEqual([]);
  });
});

describe('previewValue', () => {
  it('renders empty values as a dash, string arrays joined and long text truncated', () => {
    expect(previewValue(null)).toBe('—');
    expect(previewValue('')).toBe('—');
    expect(previewValue(['а', 'б'])).toBe('а, б');
    expect(previewValue([{ low: 1 }, { low: 2 }])).toBe('2 шт.');
    expect(previewValue('x'.repeat(200), 10)).toBe(`${'x'.repeat(10)}…`);
  });
});

describe('indexAfterRemoval', () => {
  it('selects the next item at the same position, or the previous one after removing the last', () => {
    expect(indexAfterRemoval(0, 3)).toBe(0);
    expect(indexAfterRemoval(1, 3)).toBe(1);
    expect(indexAfterRemoval(2, 3)).toBe(1);
  });

  it('returns -1 when the list becomes empty', () => {
    expect(indexAfterRemoval(0, 1)).toBe(-1);
  });
});

describe('isTypingTarget', () => {
  it('detects form fields so hotkeys do not fire while typing', () => {
    expect(isTypingTarget({ tagName: 'INPUT' } as unknown as EventTarget)).toBe(true);
    expect(isTypingTarget({ tagName: 'TEXTAREA' } as unknown as EventTarget)).toBe(true);
    expect(isTypingTarget({ tagName: 'DIV', isContentEditable: true } as unknown as EventTarget)).toBe(true);
    expect(isTypingTarget({ tagName: 'DIV' } as unknown as EventTarget)).toBe(false);
    expect(isTypingTarget(null)).toBe(false);
  });
});
