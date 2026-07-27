import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../hooks/useThread', () => ({ useThread: vi.fn() }));
vi.mock('../api/thread', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/thread')>()),
  sendMessage: vi.fn().mockResolvedValue({ messageId: 'm1', seq: 2, queued: true }),
  cancelRun: vi.fn().mockResolvedValue(undefined),
  rerunStage: vi.fn().mockResolvedValue(undefined),
}));
import type { ThreadEntry } from '../api/thread';
import * as api from '../api/thread';
import { useThread } from '../hooks/useThread';
import { AgentPanel } from './AgentPanel';

const refresh = vi.fn().mockResolvedValue(undefined);

const ready = (entries: ThreadEntry[] = []): ReturnType<typeof useThread> => ({
  entries,
  live: true,
  loading: false,
  error: null,
  refresh,
});

describe('AgentPanel', () => {
  it('says plainly when a stage has no agent', () => {
    vi.mocked(useThread).mockReturnValue(ready());
    render(<AgentPanel projectId="proj-test" stageSlug="background" stageLabel="Background" />);
    expect(screen.getByText(/no agent on this stage/i)).toBeInTheDocument();
  });

  it('sends a message and clears the composer', async () => {
    vi.mocked(useThread).mockReturnValue(ready());
    render(<AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />);
    const box = screen.getByLabelText(/message the discovery agent/i);
    await userEvent.type(box, 'why Zr?');
    await userEvent.click(screen.getByRole('button', { name: /send/i }));
    await waitFor(() =>
      expect(api.sendMessage).toHaveBeenCalledWith('proj-test', 'discovery', 'why Zr?'),
    );
    expect(box).toHaveValue('');
  });

  /**
   * The server never streams a message entry — it belongs to no run, so it has no id in the cursor
   * space a reconnect replays from. Without an explicit re-read the operator would send into a
   * timeline that visibly does nothing, which reads as the send having failed.
   */
  it('re-reads the thread after sending, because the stream will not carry the message', async () => {
    vi.mocked(useThread).mockReturnValue(ready());
    refresh.mockClear();
    render(<AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />);
    await userEvent.type(screen.getByLabelText(/message the discovery agent/i), 'why Zr?');
    await userEvent.click(screen.getByRole('button', { name: /send/i }));
    await waitFor(() => expect(refresh).toHaveBeenCalled());
  });

  /** "Nothing is happening" and "I am not being told what is happening" must be distinguishable. */
  it('says when it is not receiving live updates', () => {
    vi.mocked(useThread).mockReturnValue({
      entries: [],
      live: false,
      loading: false,
      error: null,
      refresh,
    });
    render(<AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />);
    expect(screen.getByText(/not live/i)).toBeInTheDocument();
  });

  /**
   * A run landing must announce ITS OWN outcome. Keying on "stopped running" alone would announce
   * success over a failure — in an app whose premise is that confident wrongness causes harm, that
   * is worse than the silence it replaces.
   */
  it('announces a landed run by its own outcome', async () => {
    const base = {
      runId: 'r1',
      stage: 'discovery',
      agent: 'discovery',
      subject: null,
      parentRunId: null,
      trigger: 'pipeline' as const,
      startedAt: 'x',
      error: null,
      steps: [],
    };
    vi.mocked(useThread).mockReturnValue({
      entries: [{ seq: 1, at: 'x', kind: 'run', run: { ...base, endedAt: null, outcome: 'running' } }],
      live: true,
      loading: false,
      error: null,
      refresh,
    } as ReturnType<typeof useThread>);

    const { rerender } = render(
      <AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />,
    );

    vi.mocked(useThread).mockReturnValue({
      entries: [
        {
          seq: 1,
          at: 'x',
          kind: 'run',
          run: { ...base, endedAt: 'y', outcome: 'failed', error: 'timed out' },
        },
      ],
      live: true,
      loading: false,
      error: null,
      refresh,
    } as ReturnType<typeof useThread>);
    rerender(<AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />);

    await waitFor(() => expect(screen.getByText('The discovery agent failed.')).toBeInTheDocument());
  });

  /**
   * Two backing stages means two threads server-side. An untabbed composer would silently post to
   * whichever one the code happened to pick — so the choice is on screen, and named.
   */
  it('offers Intake and Pool tabs on the merged stage, defaulting to Pool', async () => {
    vi.mocked(useThread).mockReturnValue(ready());
    render(<AgentPanel projectId="proj-test" stageSlug="intake" stageLabel="Intake & pool" />);
    expect(screen.getByRole('tab', { name: /pool/i })).toHaveAttribute('aria-selected', 'true');

    await userEvent.click(screen.getByRole('tab', { name: /intake/i }));
    await userEvent.type(screen.getByLabelText(/message/i), 'hello');
    await userEvent.click(screen.getByRole('button', { name: /send/i }));
    await waitFor(() =>
      expect(api.sendMessage).toHaveBeenCalledWith('proj-test', 'intake', 'hello'),
    );
  });
});
