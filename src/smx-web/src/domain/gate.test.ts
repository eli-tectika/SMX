import { describe, expect, it } from 'vitest';
import { cellBlockerKey, parseBlocker, type CellBlocker } from './gate';

describe('parseBlocker', () => {
  it('parses a per-cell blocker into cas, componentId and overall', () => {
    const b = parseBlocker('unreviewed: 61790-14-5|bottle (Fail)');
    expect(b).toEqual({
      kind: 'cell',
      cas: '61790-14-5',
      componentId: 'bottle',
      overall: 'Fail',
      raw: 'unreviewed: 61790-14-5|bottle (Fail)',
    });
  });

  it('keys a cell blocker as cas|componentId — the same key the matrix uses', () => {
    const b = parseBlocker('unreviewed: 39049-04-2|lid (NeedsReview)') as CellBlocker;
    expect(cellBlockerKey(b)).toBe('39049-04-2|lid');
  });

  it('treats the "incomplete" blocker as a humanised message, not a cell', () => {
    const b = parseBlocker('incomplete: not every candidate has a verdict yet');
    expect(b.kind).toBe('message');
    if (b.kind === 'message') expect(b.text).toBe('not every candidate has a verdict yet');
  });

  it('does not invent a verdict status the enum does not have', () => {
    // A malformed status must not be trusted as a cell — it falls back to a message.
    expect(parseBlocker('unreviewed: x|c (Sideways)').kind).toBe('message');
  });

  it('falls back to a plain message for anything unrecognised', () => {
    const b = parseBlocker('some new blocker shape');
    expect(b).toEqual({ kind: 'message', text: 'some new blocker shape', raw: 'some new blocker shape' });
  });
});
