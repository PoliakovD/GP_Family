import { describe, expect, it } from 'vitest';
import { safeReturnUrl } from './return-url';

describe('safeReturnUrl', () => {
  it('keeps an in-app path with query', () => {
    expect(safeReturnUrl('/health/intake/dose/42?x=1')).toBe('/health/intake/dose/42?x=1');
  });

  it('rejects absolute and protocol-relative URLs (open redirect)', () => {
    expect(safeReturnUrl('https://evil.example')).toBeNull();
    expect(safeReturnUrl('//evil.example')).toBeNull();
    expect(safeReturnUrl('/\\evil.example')).toBeNull();
  });

  it('rejects auth pages and empty values', () => {
    expect(safeReturnUrl('/login?returnUrl=/home')).toBeNull();
    expect(safeReturnUrl('/consent')).toBeNull();
    expect(safeReturnUrl('')).toBeNull();
    expect(safeReturnUrl(null)).toBeNull();
  });
});
