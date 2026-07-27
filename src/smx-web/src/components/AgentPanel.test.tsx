import { act, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ChatTurn } from '../api/types';
import { AgentPanel } from './AgentPanel';

vi.mock('../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  ApiError: class ApiError extends Error {},
  getChatThread: vi.fn(),
  sendChatMessage: vi.fn(),
}));
import * as api from '../api/client';

describe('AgentPanel', () => {
  /** A stage with no backend agent states the fact plainly — it is not mocked, so it is not badged. */
  it('says plainly when a stage has no agent', () => {
    render(<AgentPanel projectId="proj-test" stageSlug="decision" stageLabel="Decision" />);
    expect(screen.getByText(/no agent on this stage/i)).toBeInTheDocument();
  });

  /**
   * The pending turn is a genuine state change ("the agent is now working on what you just sent"),
   * not a ticking value — so unlike the poll-freshness ticker it gets a live region, once, not on
   * every poll tick that still finds the same turn pending.
   */
  it('announces a pending turn as a polite live region', async () => {
    vi.mocked(api.getChatThread).mockResolvedValue([
      {
        id: 'turn-1',
        role: 'operator',
        text: 'What did discovery find?',
        createdAt: '2026-07-27T10:00:00Z',
        toolCalls: [],
        status: 'pending',
      },
    ]);

    render(<AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />);

    const status = await screen.findByText(/working/i);
    expect(status).toHaveAttribute('role', 'status');
    expect(status).toHaveAttribute('aria-live', 'polite');
  });

  /**
   * "Agent working…" announces that a turn started; on its own that is half the feature, since
   * the pending line simply vanishing when the poll finds the answer is not something most screen
   * readers announce at all. This proves the other half — a one-shot "it landed" beacon — without
   * asserting on the reply's own text, which stays out of the live region on purpose (the same
   * re-announce-the-body reasoning that kept the streamed Interview bubble non-live).
   */
  it('announces once a pending turn resolves, and only after it resolves', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const pending: ChatTurn = {
      id: 'turn-1',
      role: 'operator',
      text: 'What did discovery find?',
      createdAt: '2026-07-27T10:00:00Z',
      toolCalls: [],
      status: 'pending',
    };
    const answered: ChatTurn = { ...pending, status: 'answered' };
    vi.mocked(api.getChatThread).mockResolvedValueOnce([pending]).mockResolvedValueOnce([answered]);

    render(<AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />);
    await screen.findByText(/agent working/i);
    // Nothing has resolved yet — the completion beacon must still be empty, or this test would
    // pass even with the effect firing on mount rather than on the pending→answered transition.
    expect(screen.queryByText(/reply received/i)).not.toBeInTheDocument();

    // usePolling's own interval, not a magic number: it re-fetches on a plain setTimeout, so the
    // next tick has to actually elapse before the "answered" thread is read. Wrapped in `act`
    // because the fake-timer tick drives a state update (the new poll result) outside of any
    // Testing Library event helper, which is the one case RTL cannot auto-wrap for you.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3000);
    });

    const status = await screen.findByText(/reply received/i);
    expect(status).toHaveAttribute('role', 'status');
    expect(status).toHaveAttribute('aria-live', 'polite');
  });

  afterEach(() => {
    vi.useRealTimers();
  });
});
