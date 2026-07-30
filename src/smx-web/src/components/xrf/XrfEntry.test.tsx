import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { XrfEntry } from './XrfEntry';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getXrfState: vi.fn(),
  parseXrf: vi.fn(),
  confirmXrf: vi.fn(),
  xrfTemplateUrl: '/api/xrf-template.csv',
}));
import * as api from '../../api/client';

const EMPTY = { components: ['bottle', 'lid'], elementPools: [], measuredBackgrounds: [], device: null };

const PARSED = {
  proposals: [{
    rowNumber: 2, component: 'bottle', element: 'Ba', line: 'Ka', status: 'V',
    signalNote: null, backgroundLevel: 12.5, backgroundUnit: 'ppm',
    deviceModel: 'Niton XL5', deviceLod: 3, deviceLodUnit: 'ppm', problems: [],
  }],
  sheetProblems: [],
};

const csv = () => new File(['component\n'], 'result.csv', { type: 'text/csv' });

beforeEach(() => {
  // Reset, not just re-stub: the last test counts CALLS, and vitest does not clear a mock's call
  // log between tests on its own — without this it sees every earlier test's mount read too.
  vi.mocked(api.getXrfState).mockReset();
  vi.mocked(api.getXrfState).mockResolvedValue(EMPTY);
  vi.mocked(api.parseXrf).mockReset();
  vi.mocked(api.confirmXrf).mockReset();
});

const show = (onConfirmed = vi.fn()) =>
  render(<XrfEntry projectId="proj-1" onConfirmed={onConfirmed} />);

describe('XRF entry', () => {
  it('offers the template, because the parser only reads one column shape', async () => {
    // A parser this strict without a template to match it is a parser that rejects every real file.
    show();
    expect(await screen.findByRole('link', { name: /template/i }))
      .toHaveAttribute('href', '/api/xrf-template.csv');
  });

  it('shows what is already confirmed, so a re-entry is visibly a re-measure', async () => {
    vi.mocked(api.getXrfState).mockResolvedValue({
      ...EMPTY,
      elementPools: [{ component: 'bottle', element: 'Ba', line: 'Ka', status: 'V' }],
    });
    show();
    expect(await screen.findByTestId('xrf-confirmed-summary')).toHaveTextContent(/Ba/);
  });

  it('says nothing is recorded yet when nothing is', async () => {
    // The absence must be STATED. A blank panel reads as "this screen has nothing to do with me".
    show();
    expect(await screen.findByTestId('xrf-confirmed-summary')).toHaveTextContent(/no|not/i);
  });

  it('renders parsed rows for confirmation after an upload', async () => {
    vi.mocked(api.parseXrf).mockResolvedValue(PARSED);
    show();

    await userEvent.upload(await screen.findByLabelText(/upload/i), csv());

    await waitFor(() => expect(screen.getByTestId('xrf-row-Ba')).toBeInTheDocument());
  });

  it('shows a rejected file as a stated fact, not as an empty table', async () => {
    // Silence after an upload reads as "it worked".
    vi.mocked(api.parseXrf).mockRejectedValue(new Error('the file is missing these columns: status'));
    show();

    await userEvent.upload(await screen.findByLabelText(/upload/i), csv());

    expect(await screen.findByRole('alert')).toHaveTextContent(/missing these columns/);
  });

  it('lets the operator start a row by hand when nothing parses', async () => {
    show();
    await userEvent.click(await screen.findByRole('button', { name: /by hand|manually/i }));
    await waitFor(() => expect(screen.getByTestId('xrf-manual-grid')).toBeInTheDocument());
  });

  it('surfaces the server’s refusal verbatim when confirm is rejected', async () => {
    // The client-side check is a convenience; the server's refusal is the contract, and paraphrasing
    // it would hide which row and which rule.
    vi.mocked(api.parseXrf).mockResolvedValue(PARSED);
    vi.mocked(api.confirmXrf).mockRejectedValue(new Error("row 2 measures component 'sleeve'"));
    show();

    await userEvent.upload(await screen.findByLabelText(/upload/i), csv());
    await waitFor(() => expect(screen.getByTestId('xrf-row-Ba')).toBeInTheDocument());
    await userEvent.click(screen.getByRole('button', { name: /confirm/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/sleeve/);
  });

  it('re-reads the record and tells the stage after a successful confirm', async () => {
    // The record is the source of truth, and the Discovery park lifts on the server's write — so the
    // screen must re-read rather than patch, and the stage above must re-read too or the operator
    // stares at "parked" for a project that just started.
    vi.mocked(api.parseXrf).mockResolvedValue(PARSED);
    vi.mocked(api.confirmXrf).mockResolvedValue({
      projectId: 'proj-1', pools: 1, backgrounds: 1, device: 'Niton XL5',
    });
    const onConfirmed = vi.fn();
    show(onConfirmed);

    await userEvent.upload(await screen.findByLabelText(/upload/i), csv());
    await waitFor(() => expect(screen.getByTestId('xrf-row-Ba')).toBeInTheDocument());
    await userEvent.click(screen.getByRole('button', { name: /confirm/i }));

    await waitFor(() => expect(onConfirmed).toHaveBeenCalled());
    expect(api.getXrfState).toHaveBeenCalledTimes(2);
  });
});

/**
 * This panel is the work area's LEFT column — 340px at a 1400px viewport, 390px above it, less
 * 12px of padding each side. Size is therefore the expensive axis here and separation is the cheap
 * one, so the promotions are limited to text that is genuinely parsed as sentences, and everything
 * that identifies something at a glance keeps the floor and takes weight instead.
 */
describe('XRF entry — read versus referenced', () => {
  it('rules the panel heading off from the sections under it', async () => {
    const { container } = show();
    await screen.findByTestId('xrf-confirmed-summary');
    expect(container.querySelectorAll('[data-testid="section-rule"]')).toHaveLength(1);
  });

  it('sets the nothing-is-written-yet rule as prose', async () => {
    // The one statement that governs the whole panel, and it was the quietest text in it.
    show();
    const rule = await screen.findByText(/Nothing is written until you confirm/);
    expect(rule.className).toContain('prose');
    expect(rule.className).not.toContain('muted');
  });

  it('explains an unrecorded background as prose, not as a caption', async () => {
    show();
    const said = await screen.findByText(/Discovery is waiting on it/);
    expect(said.className).toContain('prose');
    expect(said.className).not.toContain('secondary');
  });

  it('keeps the pool readout and its labels at the floor', async () => {
    vi.mocked(api.getXrfState).mockResolvedValue({
      ...EMPTY,
      elementPools: [{ component: 'bottle', element: 'Ba', line: 'Ka', status: 'V' }],
    });
    show();
    const summary = await screen.findByTestId('xrf-confirmed-summary');
    // The component name is a label and the counts are counts — neither is read as a sentence,
    // and at this width enlarging them would cost the chips their line.
    const label = within(summary).getByText('bottle');
    expect(label.className).toContain('tiny');
    expect(within(summary).getByText(/element.*in the pool/).className).toContain('tiny');
  });
});
