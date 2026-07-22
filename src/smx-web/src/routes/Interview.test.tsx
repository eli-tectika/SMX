import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Interview } from './Interview';
import { createBlocker } from '../domain/intakeGate';
import type { IntakeSession } from '../api/types';

vi.mock('../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getIntakeQuestions: vi.fn(),
  getIntakeSession: vi.fn(),
  createIntakeSession: vi.fn(),
  sendInterviewMessage: vi.fn(),
  uploadAttachment: vi.fn(),
}));
import * as api from '../api/client';

const QUESTIONS = [
  { id: 'raw-materials', prompt: 'What raw materials?', why: 'Discovery screens against them.' },
  { id: 'qc-tests', prompt: 'What QC tests?', why: 'They constrain what is detectable.' },
];

const session = (over: Partial<IntakeSession> = {}): IntakeSession => ({
  sessionId: 'isx-1', status: 'interviewing', client: '', product: '', summary: '',
  turns: [], attachments: [], dossier: [], proposedComponents: [],
  createdAt: '2026-07-22T10:00:00Z', updatedAt: '', ...over,
});

function renderAt(sessionId = 'isx-1') {
  return render(
    <MemoryRouter initialEntries={[`/new/${sessionId}`]}>
      <Routes>
        <Route path="new/:sessionId" element={<Interview />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.mocked(api.getIntakeQuestions).mockResolvedValue(QUESTIONS);
  vi.mocked(api.getIntakeSession).mockResolvedValue(session());
});

describe('the interview screen', () => {
  it('replays the transcript from the record, so a closed tab resumes', async () => {
    // Law 6. The MAF session cannot be rehydrated; the record IS the conversation, and the screen
    // must render it rather than starting an empty one.
    vi.mocked(api.getIntakeSession).mockResolvedValue(session({
      turns: [
        { role: 'operator', text: 'Acme, PET bottles.', toolCalls: [], createdAt: '…10:00:00Z' },
        { role: 'agent', text: 'What are the components?', toolCalls: [], createdAt: '…10:00:05Z' },
      ],
    }));
    renderAt();

    expect(await screen.findByText('Acme, PET bottles.')).toBeInTheDocument();
    expect(screen.getByText('What are the components?')).toBeInTheDocument();
  });

  it('shows how much is covered without presenting a checklist', async () => {
    vi.mocked(api.getIntakeSession).mockResolvedValue(session({
      dossier: [{ questionId: 'raw-materials', state: 'answered', answer: 'PET',
                  provenance: 'operator', recordedAt: '…' }],
    }));
    renderAt();

    // One collapsed line — the operator can open it, but is never PRESENTED with a form to fill.
    expect(await screen.findByText(/1 of 2 covered/i)).toBeInTheDocument();
    expect(screen.queryByText('What QC tests?')).not.toBeInTheDocument();
  });

  it('lists what is open only when the operator asks', async () => {
    renderAt();
    await userEvent.click(await screen.findByRole('button', { name: /see what.s open/i }));
    expect(screen.getByText(/What QC tests\?/)).toBeInTheDocument();
  });

  /**
   * The load-bearing one. The button must mirror the server's gate, so a refusal is never a surprise —
   * and it must be DISABLED while the dossier is incomplete, with the reason visible.
   */
  it('will not arm Create the project while the gate would refuse', async () => {
    renderAt();
    const create = await screen.findByRole('button', { name: /create the project/i });
    expect(create).toBeDisabled();
    // The REASON, verbatim from the mirror rather than a loose regex. A pattern like
    // /summary|component/i is matched by the button's own static hint copy, so it would keep passing
    // with the blocker text deleted — a disabled button whose reason is invisible is exactly the
    // surprise this is meant to prevent.
    const reason = createBlocker(session(), QUESTIONS);
    expect(reason).not.toBeNull();
    expect(screen.getByText(reason as string)).toBeInTheDocument();
  });

  it('streams the reply as it arrives, and keeps what the operator said', async () => {
    vi.mocked(api.sendInterviewMessage).mockImplementation(async (_id, _text, onEvent) => {
      onEvent({ event: 'chunk', data: JSON.stringify({ text: 'Good — ' }) });
      onEvent({ event: 'chunk', data: JSON.stringify({ text: 'next question.' }) });
      onEvent({ event: 'done', data: JSON.stringify({ createdProjectId: null, toolCalls: [] }) });
    });
    renderAt();

    await userEvent.type(await screen.findByRole('textbox'), 'Acme, PET bottles.');
    await userEvent.click(screen.getByRole('button', { name: /send/i }));

    // The operator's own words appear immediately — losing them to a slow or failed model call would
    // be the worst possible failure of Law 6.
    expect(await screen.findByText('Acme, PET bottles.')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText(/Good — next question\./)).toBeInTheDocument());
  });

  it('shows an unreadable attachment by name and says the agent cannot read it', async () => {
    // Design §5.2: an unreadable file is a VISIBLE FACT, never silence — on this screen too, so the
    // operator understands why the agent is about to ask them what it shows.
    vi.mocked(api.getIntakeSession).mockResolvedValue(session({
      attachments: [{ fileId: 'att-1', filename: 'line-photo.jpg', contentType: 'image/jpeg',
                      sizeBytes: 10, blobPath: 'p', status: 'unsupported',
                      error: 'there is no extractor for .jpg files' }],
    }));
    renderAt();

    expect(await screen.findByText('line-photo.jpg')).toBeInTheDocument();
    expect(screen.getByText(/couldn.t read|cannot read/i)).toBeInTheDocument();
  });
});
