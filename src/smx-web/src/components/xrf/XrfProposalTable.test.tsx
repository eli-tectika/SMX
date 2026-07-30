import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { XrfProposalTable } from './XrfProposalTable';
import type { XrfProposal } from '../../api/types';

const row = (over: Partial<XrfProposal> = {}): XrfProposal => ({
  rowNumber: 2, component: 'bottle', element: 'Ba', line: 'Ka', status: 'V',
  signalNote: null, backgroundLevel: 12.5, backgroundUnit: 'ppm',
  deviceModel: 'Niton XL5', deviceLod: 3.0, deviceLodUnit: 'ppm', problems: [], ...over,
});

const show = (proposals: XrfProposal[], onConfirm = vi.fn(), onChange = vi.fn()) =>
  render(
    <XrfProposalTable
      proposals={proposals}
      components={['bottle', 'lid']}
      onChange={onChange}
      onConfirm={onConfirm}
      busy={false}
    />,
  );

const rowFor = (element: string) => screen.getByTestId(`xrf-row-${element}`);

describe('the XRF proposals table', () => {
  it('shows every parsed row with its measurement', () => {
    show([row(), row({ element: 'Sr', rowNumber: 3, backgroundLevel: 8.1 })]);
    expect(within(rowFor('Ba')).getByDisplayValue('12.5')).toBeInTheDocument();
    expect(within(rowFor('Sr')).getByDisplayValue('8.1')).toBeInTheDocument();
  });

  it('marks an X row as recorded but not in the pool', () => {
    // X is a measurement, not an omission — and it must not read as a usable element. Asserted
    // inside the row, so the legend elsewhere on the screen cannot satisfy it.
    show([row({ element: 'Fe', status: 'X' })]);
    expect(within(rowFor('Fe')).getByTestId('xrf-pool-membership'))
      .toHaveTextContent(/not in the pool/i);
  });

  it('marks a V row as in the pool', () => {
    show([row({ element: 'Ba', status: 'V' })]);
    expect(within(rowFor('Ba')).getByTestId('xrf-pool-membership')).toHaveTextContent(/in the pool/i);
  });

  /**
   * The anti-rubber-stamping rule, on screen. A conditional verdict with no stated reason cannot be
   * reviewed, only nodded at — so the button does not arm and the row says which cell is missing.
   */
  it('will not confirm while a conditional row has no signal note', () => {
    show([row({ status: 'L', signalNote: null,
                problems: ['a conditional (L) row must carry a signal-character note.'] })]);
    expect(screen.getByRole('button', { name: /confirm/i })).toBeDisabled();
    expect(within(rowFor('Ba')).getByTestId('xrf-row-problem')).toHaveTextContent(/signal/i);
  });

  it('reports an edit to the signal note', async () => {
    // The grid is EDITABLE, unlike every other analytical surface: this is the operator's
    // transcription of a human physicist's measurement, not agent output.
    const onChange = vi.fn();
    show([row({ status: 'L', signalNote: null, problems: ['needs a note'] })], vi.fn(), onChange);

    await userEvent.type(within(rowFor('Ba')).getByLabelText(/signal note/i), 'x');

    expect(onChange).toHaveBeenCalledWith([
      expect.objectContaining({ element: 'Ba', signalNote: 'x' }),
    ]);
  });

  it('refuses to store a number it cannot read, instead of dropping it silently', async () => {
    // The failure this prevents: "12,5" is a comma-decimal habit, and it does not parse. The cell
    // emits null — but the input still SHOWS 12,5, so without a problem on the row the operator
    // watches themselves enter a measurement, confirm arms, and the record gets no background for
    // that element at all. Nothing downstream would ever say so.
    const onChange = vi.fn();
    show([row({ backgroundLevel: null })], vi.fn(), onChange);

    await userEvent.type(within(rowFor('Ba')).getByLabelText(/background level/i), '12,5');

    const emitted = onChange.mock.calls.at(-1)![0] as XrfProposal[];
    expect(emitted[0].backgroundLevel).toBeNull();
    expect(emitted[0].problems).not.toHaveLength(0);
  });

  it('clears that problem once the number becomes readable', async () => {
    // Otherwise the operator fixes the cell and the button stays dead with no way to revive it.
    const onChange = vi.fn();
    show([row({ backgroundLevel: null, problems: [] })], vi.fn(), onChange);

    const cell = within(rowFor('Ba')).getByLabelText(/background level/i);
    await userEvent.type(cell, '12,');
    expect((onChange.mock.calls.at(-1)![0] as XrfProposal[])[0].problems).not.toHaveLength(0);

    onChange.mockClear();
    await userEvent.clear(cell);
    await userEvent.type(cell, '12.5');
    const emitted = onChange.mock.calls.at(-1)![0] as XrfProposal[];
    expect(emitted[0].backgroundLevel).toBe(12.5);
    expect(emitted[0].problems).toHaveLength(0);
  });

  it('confirms with exactly the rows on screen', async () => {
    const onConfirm = vi.fn();
    const rows = [row(), row({ element: 'Sr', rowNumber: 3 })];
    show(rows, onConfirm);
    await userEvent.click(screen.getByRole('button', { name: /confirm/i }));
    expect(onConfirm).toHaveBeenCalledWith(rows);
  });

  it('says what confirming will do, in the operator’s terms', () => {
    // Confirming is what releases Discovery. An operator who does not know that has no reason to
    // press it today rather than next week, and the project sits.
    show([row()]);
    expect(screen.getByTestId('xrf-confirm-effect')).toHaveTextContent(/discovery/i);
  });

  it('sets that sentence at reading size, without widening the grid', () => {
    // "What pressing this does" is part of the decision, so it is READ. It sits in a block that
    // sizes to the scroll box rather than to the table, so reading size costs wrapping, not width.
    show([row()]);
    const effect = screen.getByTestId('xrf-confirm-effect');
    expect(effect.className).toContain('prose');
    expect(effect.className).not.toContain('muted');
  });

  it('keeps the grid’s own labels at the floor', () => {
    // Ten fixed-width columns inside a 340px column. Pool membership, units and row problems are
    // identified at a glance, and enlarging any of them widens the horizontal scroll for nothing.
    show([row({ status: 'L', signalNote: null, problems: ['needs a note'] })]);
    const membership = within(rowFor('Ba')).getByText(/in the pool/);
    expect(membership.className).toContain('tiny');
    expect(membership.className).not.toContain('prose');
    expect(within(rowFor('Ba')).getByTestId('xrf-row-problem').className).toContain('tiny');
  });

  it('drops a row when the operator removes it', async () => {
    const onChange = vi.fn();
    show([row(), row({ element: 'Sr', rowNumber: 3 })], vi.fn(), onChange);
    await userEvent.click(within(rowFor('Sr')).getByRole('button', { name: /remove/i }));
    expect(onChange).toHaveBeenCalledWith([expect.objectContaining({ element: 'Ba' })]);
  });

  it('will not confirm nothing', () => {
    // Confirming an empty set would record "the physicist found no usable element", which is a very
    // different claim from "nobody entered anything". The server refuses it too.
    show([]);
    expect(screen.getByRole('button', { name: /confirm/i })).toBeDisabled();
  });
});
