import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Interview } from './Interview';
import { createBlocker } from '../domain/intakeGate';
import type { IntakeSession, SessionAttachment } from '../api/types';

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
  // Vitest mocks are NOT reset between tests (`vite.config.ts` sets neither `resetMocks` nor
  // `clearMocks`), so a test that installs a rejecting implementation — to exercise the error
  // banner — would otherwise leak that rejection into every test that runs after it. Re-priming
  // a harmless default here keeps each test's mock behaviour scoped to what IT sets up.
  vi.mocked(api.sendInterviewMessage).mockResolvedValue(undefined);
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

  it('lets the operator dismiss a failed turn\'s error, so it cannot be mistaken for a current one', async () => {
    vi.mocked(api.sendInterviewMessage).mockRejectedValue(new Error('the model call timed out'));
    renderAt();

    await userEvent.type(await screen.findByRole('textbox'), 'Acme, PET bottles.');
    await userEvent.click(screen.getByRole('button', { name: /send/i }));

    expect(await screen.findByText('the model call timed out')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /dismiss/i }));
    expect(screen.queryByText('the model call timed out')).not.toBeInTheDocument();
  });
});

describe('Interview composer', () => {
  it('breaks the line on Ctrl+Enter and sends on plain Enter', async () => {
    const user = userEvent.setup();
    renderAt();
    const box = await screen.findByLabelText(/message the interview agent/i);
    // Vitest mocks are not reset between tests in this file, so a prior test's turn(s) would
    // otherwise still be sitting in `mock.calls` when we assert below. This test only cares about
    // what OUR interaction sends, so start from a clean slate rather than depending on run order.
    vi.mocked(api.sendInterviewMessage).mockClear();

    await user.click(box);
    await user.keyboard('first line');
    // Ctrl+Enter is inert in a real textarea, so the handler splices the newline in itself. That
    // makes this a real assertion here: with the splice deleted the box would still read one line.
    await user.keyboard('{Control>}{Enter}{/Control}');
    await user.keyboard('second line');
    expect(box).toHaveValue('first line\nsecond line');
    expect(api.sendInterviewMessage).not.toHaveBeenCalled();

    await user.keyboard('{Enter}');
    // Sending clears the draft — that is the observable fact, independent of transport. The full
    // two-line draft going out also proves the Enter that sent it did not leak its own newline.
    await waitFor(() => expect(box).toHaveValue(''));
    expect(api.sendInterviewMessage).toHaveBeenCalledTimes(1);
    expect(api.sendInterviewMessage).toHaveBeenCalledWith(
      'isx-1',
      'first line\nsecond line',
      expect.any(Function),
    );
  });

  it('leaves Shift+Enter to the browser as a newline', async () => {
    const user = userEvent.setup();
    renderAt();
    const box = await screen.findByLabelText(/message the interview agent/i);
    vi.mocked(api.sendInterviewMessage).mockClear();

    await user.click(box);
    await user.keyboard('one{Shift>}{Enter}{/Shift}two');
    expect(box).toHaveValue('one\ntwo');
    expect(api.sendInterviewMessage).not.toHaveBeenCalled();
  });

  it('shows a drop target while a file is over the composer', async () => {
    renderAt();
    const zone = await screen.findByTestId('interview-dropzone');
    expect(zone).toHaveAttribute('data-dragging', 'false');

    fireEvent.dragEnter(zone, { dataTransfer: { types: ['Files'] } });
    expect(zone).toHaveAttribute('data-dragging', 'true');
    expect(screen.getByText(/drop to hand it over/i)).toBeInTheDocument();

    fireEvent.dragLeave(zone);
    expect(zone).toHaveAttribute('data-dragging', 'false');
  });
});

/**
 * Uploading is a LOOP over the operator's selection, and every test here is about what happens when
 * one trip round that loop fails. Found live against deployed dev on 2026-07-29: dropping three
 * files where the middle one was rejected uploaded two, stored one, and showed the operator zero —
 * the screen claimed less than the record held, which is the same class of lie as claiming more.
 */
describe('Interview file upload', () => {
  const stored = (filename: string): SessionAttachment => ({
    fileId: `att-${filename}`, filename, contentType: 'application/pdf', sizeBytes: 10,
    blobPath: `p/${filename}`, status: 'extracted', textBlobPath: 't',
  });

  /** A File whose `size` is whatever the test needs — real bytes would make the fixture enormous. */
  const file = (name: string, size = 10): File => {
    const f = new File(['x'], name, { type: 'application/pdf' });
    Object.defineProperty(f, 'size', { value: size });
    return f;
  };

  const drop = (zone: HTMLElement, files: File[]) =>
    fireEvent.drop(zone, { dataTransfer: { files, types: ['Files'] } });

  beforeEach(() => {
    vi.mocked(api.uploadAttachment).mockReset();
    vi.mocked(api.uploadAttachment).mockImplementation(async (_s, f) => stored(f.name));
  });

  it('uploads the rest of the selection after one file is rejected', async () => {
    // The loop must not abandon the files behind the failure. Silently skipping them is worse than
    // refusing them: the operator watched the file go in and has no reason to look for it again.
    vi.mocked(api.uploadAttachment)
      .mockImplementationOnce(async (_s, f) => stored(f.name))
      .mockRejectedValueOnce(new Error("'huge.pdf' is larger than the 25 MB limit."))
      .mockImplementationOnce(async (_s, f) => stored(f.name));
    renderAt();
    const zone = await screen.findByTestId('interview-dropzone');

    drop(zone, [file('brief.pdf'), file('huge.pdf'), file('notes.txt')]);

    await waitFor(() => expect(api.uploadAttachment).toHaveBeenCalledTimes(3));
    expect(vi.mocked(api.uploadAttachment).mock.calls.map((c) => c[1].name))
      .toEqual(['brief.pdf', 'huge.pdf', 'notes.txt']);
  });

  it('shows a file the server did store even though another one failed', async () => {
    // The refresh has to happen on the failing path too. Without it the chip list is never re-read,
    // so a file that IS in the record — and that the agent will read — appears nowhere on screen.
    vi.mocked(api.getIntakeSession).mockResolvedValue(session({ attachments: [stored('brief.pdf')] }));
    vi.mocked(api.getIntakeSession).mockResolvedValueOnce(session());   // the mount read: still empty
    vi.mocked(api.uploadAttachment)
      .mockImplementationOnce(async (_s, f) => stored(f.name))
      .mockRejectedValueOnce(new Error('Payload Too Large'));
    renderAt();
    const zone = await screen.findByTestId('interview-dropzone');

    drop(zone, [file('brief.pdf'), file('huge.pdf')]);

    expect(await screen.findByText('brief.pdf')).toBeInTheDocument();
  });

  it('names the file that failed, even when the server message does not', async () => {
    // A bare `413 Payload Too Large` carries no body, so the message the operator would otherwise
    // get is two words about no file in particular.
    vi.mocked(api.uploadAttachment).mockRejectedValue(new Error('Payload Too Large'));
    renderAt();
    const zone = await screen.findByTestId('interview-dropzone');

    drop(zone, [file('scan.pdf')]);

    expect(await screen.findByText(/scan\.pdf/)).toBeInTheDocument();
  });

  it('does not say the filename twice when the server message already names it', async () => {
    // The server's own 422 names the file ("'huge.pdf' is larger than the 25 MB limit."). Prefixing
    // that unconditionally reads as a stutter, and a message that looks careless is a message the
    // operator skims.
    vi.mocked(api.uploadAttachment)
      .mockRejectedValue(new Error("'scan.pdf' is larger than the 25 MB limit."));
    renderAt();
    const zone = await screen.findByTestId('interview-dropzone');

    drop(zone, [file('scan.pdf')]);

    const banner = await screen.findByText(/scan\.pdf/);
    expect(banner.textContent!.match(/scan\.pdf/g)).toHaveLength(1);
  });

  it('refuses a file past the 25 MB ceiling without spending the upload', async () => {
    // Checked here as well as on the server: past ~28.6 MB the request dies at Kestrel's own body
    // limit and the operator gets a bodyless 413, so the friendly message never runs. Not sending
    // the bytes at all is also the difference between an instant answer and a minute of waiting.
    renderAt();
    const zone = await screen.findByTestId('interview-dropzone');

    drop(zone, [file('huge.pdf', 26 * 1024 * 1024)]);

    expect(await screen.findByText(/huge\.pdf.*25 MB|25 MB.*huge\.pdf/)).toBeInTheDocument();
    expect(api.uploadAttachment).not.toHaveBeenCalled();
  });

  it('still uploads the files that fit when one of the selection is too big', async () => {
    renderAt();
    const zone = await screen.findByTestId('interview-dropzone');

    drop(zone, [file('huge.pdf', 26 * 1024 * 1024), file('brief.pdf')]);

    await waitFor(() => expect(api.uploadAttachment).toHaveBeenCalledTimes(1));
    expect(vi.mocked(api.uploadAttachment).mock.calls[0][1].name).toBe('brief.pdf');
  });
});

describe('Interview live regions', () => {
  /**
   * Without this, an operator using a screen reader gets no signal at all that they are waiting:
   * the send button empties, and then nothing happens audibly until the whole reply has landed.
   */
  it('announces that the agent is working, politely', async () => {
    // A promise that never resolves keeps the screen in the "sending, no chunk yet" state so the
    // status line is observable — resolving (or rejecting) it would race the assertion below.
    vi.mocked(api.sendInterviewMessage).mockImplementation(() => new Promise(() => {}));

    const user = userEvent.setup();
    renderAt();
    const box = await screen.findByLabelText(/message the interview agent/i);
    await user.type(box, 'the client is Acme');
    await user.click(screen.getByRole('button', { name: /^send$/i }));

    // Scoped rather than `getByRole('status')` bare: the coverage counter ("0 of 2 covered") is its
    // own permanent `role="status"` region on this screen, so a singular query would throw on
    // "multiple elements" the moment both are on screen at once — which, per the task's own warning,
    // is a reason to scope the query, not a reason to strip the role from either node.
    const status = await screen.findByText(/^agent working…$/i);
    expect(status).toHaveAttribute('role', 'status');
    expect(status).toHaveAttribute('aria-live', 'polite');
  });

  /**
   * "Agent working…" says a turn started; on its own that is half the feature, because the
   * streaming bubble is deliberately silent (see the comment above it in Interview.tsx) and the
   * finished reply just sits in the transcript without announcing itself. This proves the other
   * half — a one-shot "it landed" beacon — while pinning down that the beacon carries only the
   * FACT of arrival, never the reply's own words.
   */
  it('announces once the reply has landed, without repeating its text', async () => {
    vi.mocked(api.sendInterviewMessage).mockImplementation(async (_id, _text, onEvent) => {
      onEvent({ event: 'chunk', data: JSON.stringify({ text: 'Good — next question.' }) });
      onEvent({ event: 'done', data: JSON.stringify({ createdProjectId: null, toolCalls: [] }) });
    });

    const user = userEvent.setup();
    renderAt();
    const box = await screen.findByLabelText(/message the interview agent/i);
    await user.type(box, 'the client is Acme');
    await user.click(screen.getByRole('button', { name: /^send$/i }));

    const status = await screen.findByText(/^reply received\.$/i);
    expect(status).toHaveAttribute('role', 'status');
    expect(status).toHaveAttribute('aria-live', 'polite');
    expect(status).not.toHaveTextContent(/good/i);
  });
});
