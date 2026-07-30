import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  recordLoading: vi.fn(),
}));
import { LoadingEntryForm } from './LoadingEntryForm';

const noop = () => {};

/**
 * The form that un-parks Dosing. Its labels are REFERENCED — "CAS", "Element", the field names are
 * identified at a glance — but everything that explains the number being asked for is READ, and it
 * was all set at the 12px floor in muted grey together.
 */
describe('LoadingEntryForm — what is read and what is referenced', () => {
  it('reads the explanation as prose, and refuses to mute it', () => {
    render(<LoadingEntryForm projectId="proj-1" onEntered={noop} />);
    const said = screen.getByText(/the mass fraction of the marker element/i);
    expect(said).toHaveClass('prose');
    expect(said).not.toHaveClass('muted');
    expect(said).not.toHaveClass('tiny');
  });

  it('reads the consequence of the button beside it as prose', () => {
    render(<LoadingEntryForm projectId="proj-1" onEntered={noop} />);
    const said = screen.getByText(/the agent starts over with this value/i);
    expect(said).toHaveClass('prose');
    expect(said).not.toHaveClass('muted');
  });

  it('reads the loading correction as prose, in the danger tone', async () => {
    render(<LoadingEntryForm projectId="proj-1" onEntered={noop} />);
    await userEvent.type(screen.getByLabelText('Metal loading'), '78.7');
    const said = screen.getByText(/0\.787, not 78\.7/);
    expect(said).toHaveClass('prose');
    expect(said.style.color).toBe('var(--text-danger)');
  });

  /**
   * The other half of the rule, and the one the grid depends on: the columns are
   * `minmax(140px, 1fr)`, and "Metal loading (0–1]" needs ~112px at --t-small but ~131px at
   * --t-read. The judgement and the layout agree — but only because the labels stayed referenced.
   */
  it('leaves the field labels as referenced chrome', () => {
    render(<LoadingEntryForm projectId="proj-1" onEntered={noop} />);
    for (const name of ['CAS', 'Element', 'Form', 'Metal loading (0–1]', 'Basis — required']) {
      const label = screen.getByText(name);
      expect(label).toHaveClass('tiny');
      expect(label).not.toHaveClass('prose');
    }
  });

  /** The basis is the operator's own justification — the same kind of writing as a revision reason. */
  it('composes the basis at reading size', () => {
    render(<LoadingEntryForm projectId="proj-1" onEntered={noop} />);
    const box = screen.getByLabelText('Basis');
    expect(box.style.fontSize).toBe('var(--t-read)');
  });

  /**
   * These are bare `<input>`s with no `type` attribute, so base.css's `input[type='text']` rule
   * never matches them and nothing was setting their ink — what the operator typed inherited muted
   * grey from the `.tiny muted` label naming it.
   */
  it('gives the typed values primary ink rather than the label’s', () => {
    render(<LoadingEntryForm projectId="proj-1" onEntered={noop} />);
    expect(screen.getByLabelText('CAS').style.color).toBe('var(--text-primary)');
  });

  /** A heading smaller and lighter than the paragraph under it is not a heading. */
  it('gives the form heading size, weight and a hairline', () => {
    render(<LoadingEntryForm projectId="proj-1" onEntered={noop} />);
    const heading = screen.getByText('Enter the metal loading');
    expect(heading.style.fontSize).toBe('var(--t-lead)');
    expect(heading.style.fontWeight).toBe('var(--w-semibold)');
    expect(heading.style.borderBottom).toContain('var(--border)');
  });
});
