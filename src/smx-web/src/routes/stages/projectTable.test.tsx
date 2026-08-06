import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AbsentCells, AmountValue, BoundValue, readTable, type GroupRead } from './projectTable';
import type { DosingCells } from '../../api/types';

const row = (over: Record<string, unknown> = {}) => ({
  componentId: 'bottle',
  cas: '1314-36-9',
  element: 'Y',
  form: 'oxide',
  discovery: { tier: 'A', preferred: true, rationale: 'corroborated', sources: 2 },
  regulatory: null,
  dosing: null,
  outcome: null,
  stoppedAt: null,
  stoppedReason: null,
  ...over,
});

const inTable = (node: React.ReactNode) =>
  render(
    <table>
      <tbody>
        <tr>{node}</tr>
      </tbody>
    </table>,
  );

describe('readTable — a dropped row and an unreached one are different facts', () => {
  /**
   * THE BUG FAMILY. Four times this codebase has shipped a state that needed a human rendering as a
   * state that needed nobody. Here it points at a chemical: a substance REJECTED at Regulatory has no
   * ppm window and never will, while a substance whose Dosing has simply not run yet has one coming.
   * `stoppedAt` is the only thing on the wire that separates them.
   */
  it('reads a null group with no stoppedAt as not-reached', () => {
    const { rows } = readTable({ rows: [row()] });
    expect(rows[0].dosing.kind).toBe('not-reached');
  });

  it('reads a null group with a stoppedAt as stopped, carrying where and why', () => {
    const { rows } = readTable({
      rows: [row({ stoppedAt: 'regulatory', stoppedReason: 'element gate failed product-wide' })],
    });
    expect(rows[0].dosing).toEqual({
      kind: 'stopped',
      at: 'regulatory',
      reason: 'element gate failed product-wide',
    });
  });

  /**
   * A group that arrived as something other than an object must not fall into either quiet reading.
   * Rendered as "not reached" it would promise a window that is never coming; rendered as cells it
   * would throw out of the render. It is its own, loud state.
   */
  it('reads a present-but-unreadable group as unreadable, not as absence', () => {
    const { rows } = readTable({ rows: [row({ dosing: 'oops' })] });
    expect(rows[0].dosing.kind).toBe('unreadable');
  });

  /** A row with no component is a per-component track flattened into a product-wide one. Drop it. */
  it('drops and counts a row it cannot identify rather than guessing a component', () => {
    const { rows, dropped } = readTable({
      rows: [row(), { cas: '1', element: 'Y' }, { componentId: 'lid' }],
    });
    expect(rows).toHaveLength(1);
    expect(dropped).toBe(2);
  });

  /** A payload that is not a table is an empty read, never a throw out of render. */
  it('survives a payload that is not a table at all', () => {
    expect(readTable(null).rows).toEqual([]);
    expect(readTable({ rows: 'nope' }).rows).toEqual([]);
  });
});

describe('AbsentCells — the three readings never look alike', () => {
  const stopped: GroupRead<unknown> = { kind: 'stopped', at: 'regulatory', reason: 'rejected by the operator' };
  const notReached: GroupRead<unknown> = { kind: 'not-reached' };
  const unreadable: GroupRead<unknown> = { kind: 'unreadable' };

  /**
   * Asserted on `data-absence` and on the WORDS, not on colour. "They read differently" is exactly
   * the assertion that stayed green while the distinction rotted out of the spine three times.
   */
  it('states where a dropped row stopped, and why', () => {
    inTable(<AbsentCells state={stopped as never} span={4} phase="Dosing" />);
    const cell = document.querySelector('[data-absence="stopped"]');
    expect(cell).toBeInTheDocument();
    expect(cell?.textContent).toMatch(/stopped at Regulatory/);
    expect(cell?.textContent).toMatch(/rejected by the operator/);
  });

  it('says a phase has not run, rather than leaving the cells blank', () => {
    inTable(<AbsentCells state={notReached as never} span={4} phase="Dosing" />);
    const cell = document.querySelector('[data-absence="not-reached"]');
    expect(cell?.textContent).toMatch(/not reached/i);
    expect(cell?.textContent).toMatch(/Dosing has not run/i);
    // The distinguishing fact: it must NOT claim the row stopped anywhere.
    expect(cell?.textContent).not.toMatch(/stopped/i);
  });

  it('reports an unreadable group loudly, and refuses to call it a finding', () => {
    inTable(<AbsentCells state={unreadable as never} span={4} phase="Dosing" />);
    const cell = document.querySelector('[data-absence="unreadable"]');
    expect(cell).toHaveAttribute('role', 'alert');
    expect(cell?.textContent).toMatch(/cannot read/i);
    expect(cell?.textContent).toMatch(/having found nothing/i);
  });
});

describe('the cells that carry provenance and money', () => {
  /**
   * Provenance travels in the WORD, not only in the chart's geometry and never in hue alone: the
   * teal/grey encoding failed CVD validation at ΔE 4.3 under protanopia, and the difference it carries
   * is the physicist's measurement versus the agent's own guess.
   */
  it('prints each bound with its kind', () => {
    inTable(
      <td>
        <BoundValue bound={{ ppm: 40, basis: 'device LOD', kind: 'measured', confidence: 1 }} />
      </td>,
    );
    expect(screen.getByText('measured')).toBeInTheDocument();
    expect(document.querySelector('[data-bound-kind="measured"]')).toBeInTheDocument();
  });

  it('refuses to print a bound whose provenance it cannot read', () => {
    inTable(
      <td>
        <BoundValue bound={{ ppm: 40 }} />
      </td>,
    );
    expect(screen.getByText(/unreadable/i)).toBeInTheDocument();
    expect(screen.queryByText('40')).not.toBeInTheDocument();
  });

  /**
   * `ProjectTable.cs` writes `marker?.CompoundMassMg ?? 0` for a substance in no code. A rendered
   * "0.00 mg" in the column procurement reads from is a purchase quantity nobody computed — the same
   * shape as a malformed price rendering as `0.00` through `Intl.format`.
   */
  it('renders a zero order amount as an absence, never as a quantity', () => {
    const cells = {
      floor: { ppm: 1, basis: '', kind: 'measured', confidence: 1 },
      upper: { ppm: 9, basis: '', kind: 'estimate', confidence: 0.5 },
      recommendedPpm: 5,
      compoundMassMg: 0,
      suppliers: [],
      risks: [],
    } as unknown as DosingCells;
    inTable(
      <td>
        <AmountValue cells={cells} />
      </td>,
    );
    expect(screen.getByText(/not in a code/i)).toBeInTheDocument();
    expect(screen.queryByText('0')).not.toBeInTheDocument();
  });
});
