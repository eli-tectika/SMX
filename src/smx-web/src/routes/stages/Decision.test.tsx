import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import type { ProjectSummary } from '../../api/types';
import { Decision } from './Decision';

const PROJECT: ProjectSummary = {
  projectId: 'proj-test',
  client: 'MUFE',
  product: 'clear bottle',
  stages: {},
};

function decision() {
  return render(
    <MemoryRouter>
      <Decision project={PROJECT} />
    </MemoryRouter>,
  );
}

describe('Decision — the VP gate as a record', () => {
  /**
   * The load-bearing one.
   *
   * `data-provenance="mock"` is not a styling hook. The hatched surface hangs off it
   * (craft.css), and so — the half that matters — do the black margin rule and the printed
   * "MOCK DATA — NOT AGENT-PRODUCED … NOT FOR REGULATORY USE" footer (print.css). This screen's
   * codes, ppm values and clearances are fixture data, and its subject is the signature that
   * releases procurement.
   *
   * A redesign that split the page into sibling sections would leave the attribute on only one
   * of them, and the printed disclaimer would vanish silently — at which point a fabricated
   * determination prints as cleanly as a real one and can be walked into a room in a folder.
   * So: exactly one `.screen`, and the provenance is on it.
   */
  it('keeps the whole record under one mock-provenance surface', () => {
    decision();
    const screens = document.querySelectorAll('.screen');
    expect(screens).toHaveLength(1);
    expect(screens[0]).toHaveAttribute('data-provenance', 'mock');
  });

  it('says in words that no agent produced this', () => {
    decision();
    expect(screen.getByText(/Mock data/i)).toBeInTheDocument();
    expect(screen.getByText(/No decision agent has run/i)).toBeInTheDocument();
  });

  /**
   * The VP gate has no endpoint, so it must never look signable — not at any arming level.
   * The `vp` requirement is permanently unmet by construction, which keeps the meter below
   * full; the button is inert because no `onSign` is wired (see Gate.tsx). Both halves are
   * the honesty, and a future "let's make the demo feel complete" pass must trip on this.
   */
  it('never offers a signature it cannot record', () => {
    decision();
    expect(screen.getByRole('button', { name: /Approve & close project/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /Reject/i })).toBeDisabled();
    expect(screen.getByText(/No endpoint exists to record one/i)).toBeInTheDocument();
  });

  /**
   * Inert is not the same as broken, and the difference has to be visible: a meter stuck below
   * full with a greyed button reads as a failure. What makes the stall legible as DELIBERATE is
   * the gate naming the missing capability ("no endpoint exists to record one"), asserted above.
   *
   * What it must NOT do is dress the stall up as a park — the tempting move, since a gate waiting
   * on the VP reads like one. It isn't: there is no `decision` stage in the record, no VP park
   * state, and the dispatcher writes exactly three awaiting-* states (awaiting-RE,
   * awaiting-physics, awaiting-operator), none of them here. A park implies a real record stopped
   * on a named human who can unblock it; an unbuilt gate is an absent capability. Selling the
   * second as the first invents backend behaviour on a screen a client reads, which is the one
   * thing this codebase must not do. Intake and Background each carried exactly that fiction
   * until ParkSlot was deleted; this asserts the sibling screen never grows its own.
   */
  it('explains the stall as an absent capability and never as a park', () => {
    decision();
    expect(screen.getByText(/No endpoint exists to record one/i)).toBeInTheDocument();
    expect(screen.queryByText(/parks here in the real system/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/awaiting VP/i)).not.toBeInTheDocument();
  });

  /**
   * Summary-first: the determination's state is readable in the first band, without a
   * signature being faked to express it. The absent tile renders an em-dash, never a verdict.
   */
  it('reports the determination as absent rather than pending or approved', () => {
    decision();
    const tile = document.querySelector('.stat--absent');
    expect(tile).toBeInTheDocument();
    expect(tile).toHaveTextContent('VP determination');
    expect(tile).toHaveTextContent('—');
  });

  /** The evidence stays traceable to the stage that produced each criterion (spec §4.7). */
  it('links every criterion back to its owning stage', () => {
    decision();
    expect(screen.getAllByRole('button', { name: 'View' })).toHaveLength(4);
  });
});
