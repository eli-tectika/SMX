import { describe, expect, it } from 'vitest';
import { agoLabel } from './relativeTime';

describe('agoLabel', () => {
  it('reads in seconds under a minute', () => {
    expect(agoLabel(0)).toBe('just now');
    expect(agoLabel(12_000)).toBe('12s ago');
    expect(agoLabel(59_000)).toBe('59s ago');
  });

  it('reads in minutes under an hour', () => {
    expect(agoLabel(60_000)).toBe('1m ago');
    expect(agoLabel(3_599_000)).toBe('59m ago');
  });

  it('reads in hours beyond that', () => {
    expect(agoLabel(3_600_000)).toBe('1h ago');
    expect(agoLabel(7_200_000)).toBe('2h ago');
  });

  /** A clock that has drifted backwards must not print "-3s ago". */
  it('clamps a negative elapsed time to just now', () => {
    expect(agoLabel(-5_000)).toBe('just now');
  });
});
