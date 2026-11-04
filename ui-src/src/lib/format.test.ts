import { describe, expect, it } from 'vitest';
import { DASH, formatBytes, formatDuration, formatPercent, formatSecPerToken, formatTokensPerSecond, worstCaseSeconds } from './format';

describe('format', () => {
  it('shows a missing value as a dash, never a number', () => {
    for (const f of [formatDuration, formatBytes, formatSecPerToken, formatTokensPerSecond, formatPercent]) {
      expect(f(null)).toBe(DASH);
      expect(f(undefined)).toBe(DASH);
      expect(f(Number.NaN)).toBe(DASH);
    }
    expect(formatSecPerToken(0)).toBe(DASH);
    expect(worstCaseSeconds(256, null)).toBeNull();
  });
  it('formats durations', () => {
    expect(formatDuration(8.7)).toBe('8.7 s');
    expect(formatDuration(349)).toBe('5 min 49 s');
    expect(formatDuration(3600)).toBe('1 h');
    expect(formatDuration(37 * 60 + 12)).toBe('37 min 12 s');
    expect(formatDuration(2 * 3600 + 30 * 60)).toBe('2 h 30 min');
  });
  it('formats decimal gigabytes like the playbook', () => {
    expect(formatBytes(42_520_395_200)).toBe('42.5 GB');
    expect(formatBytes(2_000_000_000)).toBe('2.00 GB');
    expect(formatBytes(1_500_000)).toBe('1.5 MB');
  });
  it('formats speed both ways', () => {
    expect(formatSecPerToken(8.7)).toBe('8.70 s/token');
    expect(formatSecPerToken(12.34)).toBe('12.3 s/token');
    expect(formatTokensPerSecond(8)).toBe('0.13 tokens/s');
    expect(formatTokensPerSecond(0.1)).toBe('10.0 tokens/s');
    expect(formatPercent(0.75)).toBe('75%');
  });
  it('works out the worst case of a reply', () => {
    expect(worstCaseSeconds(256, 8.7)).toBeCloseTo(2227.2);
    expect(worstCaseSeconds(0, 8.7)).toBeNull();
  });
});
