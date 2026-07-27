import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
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
});
