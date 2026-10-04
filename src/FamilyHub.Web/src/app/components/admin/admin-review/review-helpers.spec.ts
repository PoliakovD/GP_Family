import { describe, expect, it } from 'vitest';
import { KbChangeTarget, KbVerificationStatus } from '../../../services/admin-api.service';
import {
  confidenceLabel, diffPayload, diffSnapshots, historyActionLabel, historyTargetLabel, indexAfterRemoval, isTypingTarget, payloadFieldLabel,
  parseThreshold, previewValue, reviewKindLabel, reviewOriginLabel, reviewStageLabel, sourceKindLabel, verificationLabel, verificationTagClass,
  joinAliasLines, splitAliasLines,
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

describe('verification badge helpers (admin-only marker)', () => {
  it('labels every status and marks stale verification', () => {
    expect(verificationLabel(KbVerificationStatus.AiUnverified)).toBe('Не проверено');
    expect(verificationLabel(KbVerificationStatus.AdminVerified)).toBe('Проверено');
    expect(verificationLabel(KbVerificationStatus.AdminEdited)).toBe('Проверено с правками');
    expect(verificationLabel(KbVerificationStatus.ManualKnowledge)).toBe('Знание эксперта');
    expect(verificationLabel(KbVerificationStatus.AdminVerified, true)).toBe('Проверено (устарело)');
    // «Не проверено» не бывает устаревшим — проверять нечего.
    expect(verificationLabel(KbVerificationStatus.AiUnverified, true)).toBe('Не проверено');
  });

  it('warns about unverified and stale records, greens verified ones', () => {
    expect(verificationTagClass(KbVerificationStatus.AiUnverified)).toBe('tag-warning');
    expect(verificationTagClass(KbVerificationStatus.AdminVerified, true)).toBe('tag-warning');
    expect(verificationTagClass(KbVerificationStatus.AdminVerified)).toBe('tag-ok');
    expect(verificationTagClass(KbVerificationStatus.ManualKnowledge)).toBe('tag-accent');
  });
});

describe('history and source labels', () => {
  it('maps journal actions and targets, falling back to the raw value', () => {
    expect(historyActionLabel('admin-edit')).toBe('Правка админа');
    expect(historyActionLabel('something-new')).toBe('something-new');
    expect(historyTargetLabel(KbChangeTarget.LabAnalyteKb)).toBe('Показатель');
    expect(reviewStageLabel('search')).toBe('Поиск');
    expect(reviewStageLabel('result')).toBe('Результат');
  });

  it('describes manual sources', () => {
    expect(sourceKindLabel('expert-knowledge', 'Manual')).toBe('знание эксперта');
    expect(sourceKindLabel('manual-quote', 'Manual')).toBe('ручная цитата');
    expect(sourceKindLabel(null, 'Auto')).toBeNull();
  });
});

describe('diffSnapshots', () => {
  const kb = (over: Record<string, unknown>) => JSON.stringify({
    displayName: 'АЧТВ', source: 'brave: helix.ru', aliases: ['aptt'], lockedFields: [], verificationStatus: 0,
    payloadJson: JSON.stringify({ plainExplanation: 'старое', defaultUnit: 'сек' }), ...over,
  });

  it('lists changed kb fields with human labels, including the verification status', () => {
    const after = kb({
      displayName: 'АЧТВ (кровь)', verificationStatus: 2, lockedFields: ['payload.plainExplanation'],
      payloadJson: JSON.stringify({ plainExplanation: 'новое', defaultUnit: 'сек' }),
    });
    const lines = diffSnapshots(kb({}), after);
    const labels = lines.map((l) => l.label);
    expect(labels).toContain('Название');
    expect(labels).toContain('Статус проверки');
    expect(labels).toContain('Залочено');
    expect(labels).toContain('Простыми словами');
    expect(lines.find((l) => l.label === 'Статус проверки')).toEqual({
      label: 'Статус проверки', before: 'Не проверено', after: 'Проверено с правками',
    });
  });

  it('treats a missing before-snapshot as a creation and a missing after-snapshot as a deletion', () => {
    expect(diffSnapshots(null, kb({})).some((l) => l.label === 'Простыми словами')).toBe(true);
    expect(diffSnapshots(kb({}), null).find((l) => l.label === 'Название')?.after).toBe('—');
  });

  it('describes cache snapshots by added, removed and pinned snippets', () => {
    const cache = (snippets: unknown[]) => JSON.stringify({ snippetsJson: JSON.stringify(snippets), overridesJson: null });
    const a = { url: 'https://a.ru', title: 'A', pinned: false, origin: 'Auto' };
    const b = { url: 'https://b.ru', title: 'B', pinned: false, origin: 'Manual' };
    const lines = diffSnapshots(cache([a]), cache([{ ...a, pinned: true }, b]));
    expect(lines.map((l) => l.label)).toEqual(['Источник добавлен', 'Закрепление']);
    expect(diffSnapshots(cache([a, b]), cache([a])).map((l) => l.label)).toEqual(['Источник удалён']);
  });

  it('treats a cache snapshot with displayName as cache and diffs name, units and titles', () => {
    const cache = (over: Record<string, unknown>) => JSON.stringify({
      snippetsJson: JSON.stringify([{ url: 'https://a.ru', title: 'A', pinned: false, origin: 'Auto' }]),
      overridesJson: null, displayName: 'СРБ', units: null, provider: 'brave', ...over,
    });
    const after = cache({
      displayName: 'С-реактивный белок', units: 'мг/л',
      snippetsJson: JSON.stringify([{ url: 'https://a.ru', title: 'A2', pinned: false, origin: 'Auto' }]),
    });
    expect(diffSnapshots(cache({}), after).map((l) => l.label)).toEqual(['Заголовок источника', 'Название', 'Единицы']);
  });

  it('returns nothing for invalid or empty snapshots', () => {
    expect(diffSnapshots(null, null)).toEqual([]);
    expect(diffSnapshots('not json', '{{')).toEqual([]);
  });
});

describe('parseThreshold', () => {
  it('accepts numbers and strings in [0..1], including a comma decimal separator', () => {
    expect(parseThreshold(0.7)).toBe(0.7);
    expect(parseThreshold('0,85')).toBe(0.85);
    expect(parseThreshold('0')).toBe(0);
    expect(parseThreshold(1)).toBe(1);
  });

  it('rejects empty, non-numeric and out-of-range values', () => {
    expect(parseThreshold('')).toBeNull();
    expect(parseThreshold(null)).toBeNull();
    expect(parseThreshold('abc')).toBeNull();
    expect(parseThreshold(1.01)).toBeNull();
    expect(parseThreshold(-0.1)).toBeNull();
    expect(parseThreshold(NaN)).toBeNull();
  });
});

describe('splitAliasLines / joinAliasLines', () => {
  it('keeps commas inside an alias — one alias per line', () => {
    const raw = 'УИБК\r\nНенасыщенная железосвязывающая способность, молярная концентрация в сыворотке или плазме крови\n\n  НЖСС  ';
    expect(splitAliasLines(raw)).toEqual([
      'УИБК',
      'Ненасыщенная железосвязывающая способность, молярная концентрация в сыворотке или плазме крови',
      'НЖСС',
    ]);
  });

  it('round-trips stored aliases', () => {
    const aliases = ['уибк', 'нжсс, молярная'];
    expect(splitAliasLines(joinAliasLines(aliases))).toEqual(aliases);
  });
});