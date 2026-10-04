import { describe, expect, it } from 'vitest';
import { diffLines } from './text-diff';

describe('diffLines', () => {
  it('marks removed and added lines around unchanged ones', () => {
    expect(diffLines('a\nb\nc', 'a\nB\nc\nd')).toEqual([
      { kind: 'same', text: 'a' },
      { kind: 'removed', text: 'b' },
      { kind: 'added', text: 'B' },
      { kind: 'same', text: 'c' },
      { kind: 'added', text: 'd' },
    ]);
  });

  it('returns only same lines for equal texts', () => {
    expect(diffLines('x\ny', 'x\ny').every((l) => l.kind === 'same')).toBe(true);
  });
});
