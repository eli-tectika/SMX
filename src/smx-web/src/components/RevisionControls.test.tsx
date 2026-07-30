import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  getRevisions: vi.fn(),
  reviseStage: vi.fn(),
}));
import * as api from '../api/client';
import type { RevisionDoc } from '../api/types';
import { RevisionTrail, ReviseForm } from './RevisionControls';

const revision = (over: Partial<RevisionDoc> = {}): RevisionDoc => ({
  id: 'rev-1',
  projectId: 'proj-1',
  stage: 'regulatory',
  target: 'the Zr tier',
  reason: 'The R.E. determined Zr is cleared for food contact in the EU.',
  status: 'applied',
  createdAt: '2026-07-29T10:00:00Z',
  ...over,
});

beforeEach(() => {
  vi.mocked(api.getRevisions).mockResolvedValue([revision()]);
});

/**
 * "No direct edits — tell the agent WHY" is the mechanism by which this system gets smarter, and the
 * reason is the whole payload. It was being composed, and then read back, at the 12px floor in muted
 * grey — the quietest text on the screen carrying the highest-value content.
 */
describe('RevisionControls — what is read and what is referenced', () => {
  it('composes the reason at reading size', async () => {
    render(<ReviseForm projectId="proj-1" stage="regulatory" fixedTarget="the Zr tier" />);
    await userEvent.click(screen.getByRole('button', { name: /ask the agent to revise/i }));
    const box = screen.getByLabelText(/revision reason/i);
    expect(box.style.fontSize).toBe('var(--t-read)');
    expect(box.style.lineHeight).toBe('var(--lh-prose)');
  });

  /** The target is a NAME — identified at a glance, not parsed. It stays at the floor. */
  it('leaves the target label as referenced chrome', async () => {
    render(<ReviseForm projectId="proj-1" stage="regulatory" fixedTarget="the Zr tier" />);
    await userEvent.click(screen.getByRole('button', { name: /ask the agent to revise/i }));
    const label = screen.getByText(/revising:/i);
    expect(label).toHaveClass('tiny');
    expect(label).not.toHaveClass('prose');
  });

  it('reads the recorded reason back as prose, and refuses to mute it', async () => {
    render(<RevisionTrail projectId="proj-1" />);
    const reason = await screen.findByText(/the R\.E\. determined Zr is cleared/i);
    expect(reason).toHaveClass('prose');
    expect(reason).not.toHaveClass('muted');
    expect(reason).not.toHaveClass('tiny');
  });

  it('reads a failure back as prose, in the danger tone', async () => {
    vi.mocked(api.getRevisions).mockResolvedValue([
      revision({ status: 'failed', error: 'the regulatory agent could not re-run' }),
    ]);
    render(<RevisionTrail projectId="proj-1" />);
    const failure = await screen.findByText(/could not re-run/i);
    expect(failure).toHaveClass('prose');
    expect(failure.style.color).toBe('var(--text-danger)');
  });

  /** A heading lighter and smaller than its rows is a stray label, not a heading. */
  it('gives the trail heading size, weight and a hairline', async () => {
    render(<RevisionTrail projectId="proj-1" />);
    const heading = await screen.findByText(/revision trail/i);
    expect(heading.style.fontSize).toBe('var(--t-body)');
    expect(heading.style.fontWeight).toBe('var(--w-semibold)');
    expect(heading.style.borderBottom).toContain('var(--border)');
  });

  /** The other half of the rule: what was revised and where it got to are scanned. */
  it('leaves the target and status line as referenced chrome', async () => {
    render(<RevisionTrail projectId="proj-1" />);
    const line = await screen.findByText(/the Zr tier/i);
    expect(line.closest('.small')).not.toBeNull();
  });
});
