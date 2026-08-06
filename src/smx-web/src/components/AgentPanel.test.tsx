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
   * ONE AGENT, ONE CONVERSATION — and the property that survives the tab strip's deletion is the one
   * that mattered: the operator can always tell who they are talking to, and the message reaches it.
   *
   * The strip appeared whenever a phase had more than one backing stage, which made Discovery look
   * like two agents ("pool" and "discovery" as separate correspondents) when they are two PASSES of
   * one. The composer names the PHASE now, and posts to the phase's declared agent stage.
   */
  it('presents one agent and one composer on a phase with two backing stages', async () => {
    vi.mocked(useThread).mockReturnValue(ready());
    render(<AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />);

    expect(screen.queryAllByRole('tab')).toHaveLength(0);
    expect(screen.queryByRole('tablist')).toBeNull();

    await userEvent.type(screen.getByLabelText('Message the discovery agent'), 'hello');
    await userEvent.click(screen.getByRole('button', { name: /send/i }));
    await waitFor(() =>
      expect(api.sendMessage).toHaveBeenCalledWith('proj-test', 'discovery', 'hello'),
    );
  });

  /**
   * The pool thread is still READ. Dropping `pool` from `isChatStage` would have been the easy way to
   * one tab, and it would have made every run recorded against that stage invisible — the
   * absence-reads-as-nothing-happened family this codebase keeps having to fix. Both passes' runs
   * land in one chronological timeline, which is what one agent's work should look like.
   */
  it('still shows the runs of every backing stage, merged into one timeline', () => {
    const run = (runId: string, stage: string, at: string) => ({
      seq: 1,
      at,
      kind: 'run' as const,
      run: {
        runId,
        stage,
        agent: 'discovery',
        subject: null,
        parentRunId: null,
        trigger: 'pipeline' as const,
        startedAt: at,
        endedAt: at,
        outcome: 'done' as const,
        error: null,
        steps: [],
      },
    });
    vi.mocked(useThread).mockImplementation(
      (_projectId: string, stage: string) =>
        ready([
          stage === 'pool' ? run('r-pool', 'pool', '2026-08-01T09:00:00Z') : run('r-disc', 'discovery', '2026-08-01T10:00:00Z'),
        ]) as ReturnType<typeof useThread>,
    );
    render(<AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />);
    expect(useThread).toHaveBeenCalledWith('proj-test', 'pool');
    expect(useThread).toHaveBeenCalledWith('proj-test', 'discovery');
  });

  /**
   * Regulatory's pair was worse than Discovery's: a tab for `matrix`, which holds no tools at all
   * (ToolBox returns an empty list for it), so the composer's default target was an agent that does
   * not exist and the box literally read "Message the matrix agent". It posts to `regulatory`, which
   * is what the phase's `agentStage` has declared all along.
   */
  it('targets the phase’s declared agent stage, never its last backing stage', async () => {
    vi.mocked(useThread).mockReturnValue(ready());
    render(<AgentPanel projectId="proj-test" stageSlug="regulatory" stageLabel="Regulatory" />);
    expect(screen.queryByLabelText(/matrix agent/i)).toBeNull();
    await userEvent.type(screen.getByLabelText('Message the regulatory agent'), 'why Fail?');
    await userEvent.click(screen.getByRole('button', { name: /send/i }));
    await waitFor(() =>
      expect(api.sendMessage).toHaveBeenCalledWith('proj-test', 'regulatory', 'why Fail?'),
    );
  });

  /**
   * Overview has no `STAGES` row — `intake` is not a phase — so its threads are handed in. Without
   * the override the panel would show the no-agent copy on the screen where requirements are amended.
   */
  it('accepts an explicit thread list for a screen STAGES does not know', async () => {
    vi.mocked(useThread).mockReturnValue(ready());
    render(
      <AgentPanel
        projectId="proj-test"
        stageSlug="overview"
        stageLabel="Intake"
        stages={['intake']}
      />,
    );
    await userEvent.type(screen.getByLabelText('Message the intake agent'), 'we now ship to Japan');
    await userEvent.click(screen.getByRole('button', { name: /send/i }));
    await waitFor(() =>
      expect(api.sendMessage).toHaveBeenCalledWith('proj-test', 'intake', 'we now ship to Japan'),
    );
  });
});
