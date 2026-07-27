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

const ready = (entries: ThreadEntry[] = []): ReturnType<typeof useThread> => ({
  entries,
  live: true,
  loading: false,
  error: null,
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

  /** "Nothing is happening" and "I am not being told what is happening" must be distinguishable. */
  it('says when it is not receiving live updates', () => {
    vi.mocked(useThread).mockReturnValue({
      entries: [],
      live: false,
      loading: false,
      error: null,
    });
    render(<AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />);
    expect(screen.getByText(/not live/i)).toBeInTheDocument();
  });
});
