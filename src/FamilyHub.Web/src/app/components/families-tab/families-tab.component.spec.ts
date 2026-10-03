import { describe, expect, it } from 'vitest';
import { extractInviteCode } from './families-tab.component';

describe('extractInviteCode', () => {
  it('returns a bare code as is (trimmed)', () => {
    expect(extractInviteCode('  AbC123 ')).toBe('AbC123');
  });

  it('extracts the code from a full invite link', () => {
    expect(extractInviteCode('https://familyhub.example/join/AbC123')).toBe('AbC123');
  });

  it('ignores query string and fragment after the code', () => {
    expect(extractInviteCode('https://x.ru/join/AbC123?utm=tg#top')).toBe('AbC123');
  });

  it('empty input → empty string', () => {
    expect(extractInviteCode('   ')).toBe('');
  });
});
