import { useEffect, useLayoutEffect, useRef, useState, type DragEvent } from 'react';
import { useLocation, useNavigate, useParams } from 'react-router-dom';
import {
  NotFound,
  createIntakeSession,
  getIntakeQuestions,
  getIntakeSession,
  sendInterviewMessage,
  uploadAttachment,
} from '../api/client';
import type { IntakeQuestion, IntakeSession, InterviewTurn } from '../api/types';
import { AttachmentChip } from '../components/AttachmentChip';
import { BackLink } from '../components/BackLink';
import { coverage, createBlocker } from '../domain/intakeGate';
import { useStickToBottom } from '../hooks/useStickToBottom';

/**
 * "New project" is a conversation, not a form.
 *
 * The operator talks; the interview agent interrogates them against the question catalogue, records
 * what they say into the dossier, proposes the component breakdown, and — when the server-side gate
 * passes — calls its own `create_project` tool. Nothing on this screen writes to the record directly:
 * every fact here arrived through the agent, so changing one means telling the agent why (Law 4).
 */
export function Interview() {
  const { sessionId } = useParams<{ sessionId?: string }>();
  const navigate = useNavigate();
  const { state: entryState } = useLocation();
  const [questions, setQuestions] = useState<IntakeQuestion[]>([]);
  const [session, setSession] = useState<IntakeSession | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const [streaming, setStreaming] = useState<string | null>(null); // the agent turn being received
  const [sending, setSending] = useState(false);
  // A one-shot completion beacon (sr-only — see the render below). "Agent working…" announces that
  // a turn STARTED; on its own that is half the feature, because the streaming bubble is
  // deliberately silent (see the comment above it) and the finished reply just sits in the
  // transcript without announcing itself. This is cleared at the top of every `send()` and set once
  // the reply lands at the bottom of it, so it always changes value exactly twice per turn — never
  // holds still and never re-announces the reply's own words, only the fact that it arrived.
  const [turnAnnouncement, setTurnAnnouncement] = useState('');
  const [uploading, setUploading] = useState(false);
  const [openShown, setOpenShown] = useState(false);
  const [dragging, setDragging] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  const composer = useRef<HTMLTextAreaElement>(null);
  // Where the caret must land after a Ctrl+Enter line break. Setting `draft` re-renders the
  // controlled textarea, which parks the caret at the end of the value; this puts it back. It has
  // to be restored in a LAYOUT effect, not a rAF — a rAF lands after the operator's next few
  // keystrokes have already gone in at the wrong offset.
  const caret = useRef<number | null>(null);
  useLayoutEffect(() => {
    if (caret.current === null) return;
    composer.current?.setSelectionRange(caret.current, caret.current);
    caret.current = null;
  });
  // `dragenter`/`dragleave` fire for every child the pointer crosses, so the depth is counted
  // rather than toggled — otherwise the highlight flickers off as the cursor passes the textarea.
  const dragDepth = useRef(0);
  // `streaming` is a dep so the view follows the reply as it arrives token by token, not only
  // once the finished turn lands. `sending` looks redundant next to `turns.length` — React 18
  // batches `setSending(true)` with the optimistic operator turn, so `turns.length` has usually
  // already moved in that commit — but it is the ONLY dep that changes on the empty-reply failure
  // path: the stream produces no text, so no turn is ever appended, and the sole visible change is
  // `sending` flipping back to false as the "Agent working…" line disappears. Without it here,
  // that commit shrinks the transcript with nothing re-measuring the scroll position.
  const scroller = useStickToBottom<HTMLDivElement>([session?.turns.length, streaming, sending]);

  // Safety net for the drag counter above. Crossing between elements INSIDE the page nets to zero
  // correctly via dragenter/dragleave, but the counter never sees a final dragleave when the drag
  // ends outside the page entirely — the operator drags off the browser window, or cancels with
  // Escape — because several browsers don't reliably fire one at the document boundary. Without
  // this, `dragDepth` would stay positive and the "Drop to hand it over" overlay would sit over the
  // textarea, inert, for the rest of the session. `blur` catches "left the window"; `dragend` on the
  // document catches the drag session ending (cancel or drop) by any means the per-element handlers
  // below might have missed.
  useEffect(() => {
    const reset = () => {
      dragDepth.current = 0;
      setDragging(false);
    };
    window.addEventListener('blur', reset);
    document.addEventListener('dragend', reset);
    return () => {
      window.removeEventListener('blur', reset);
      document.removeEventListener('dragend', reset);
    };
  }, []);

  // No sessionId in the URL: mint one and put it there. The id lives in the URL, not in component
  // state, precisely so a reload, a bookmark or a closed tab all resume the SAME interview — Law 6.
  // `replace` so Back does not walk into a /new that mints a second session.
  useEffect(() => {
    if (sessionId) return;
    let cancelled = false;
    createIntakeSession()
      .then(({ sessionId: id }) => {
        // The state is carried across: it is what lets the way out name where it came from,
        // and this replace is the only thing standing between the two.
        if (!cancelled) navigate(`/new/${id}`, { replace: true, state: entryState });
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [sessionId, navigate, entryState]);

  useEffect(() => {
    if (!sessionId) return;
    Promise.all([getIntakeQuestions(), getIntakeSession(sessionId)])
      .then(([qs, s]) => {
        setQuestions(qs);
        // NotFound is a real error here, NOT an empty interview. Silently starting a second
        // conversation would strand the operator in one nobody can find.
        if (s === NotFound) setError('This interview has expired or never existed.');
        else setSession(s);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [sessionId]);

  async function refresh(id: string): Promise<IntakeSession | null> {
    const refreshed = await getIntakeSession(id);
    if (refreshed === NotFound) return null;
    setSession((s) => adopt(s, refreshed));
    return refreshed;
  }

  async function send(text: string) {
    if (!sessionId || !text.trim() || sending) return;
    setSending(true);
    setDraft('');
    // Clear the PREVIOUS turn's beacon before this one starts. An aria-live region that holds the
    // same text across two turns does not re-announce on the second — without this, back-to-back
    // replies would go silent after the first "Reply received."
    setTurnAnnouncement('');
    // The operator's own words go on screen IMMEDIATELY. The server persists them before the model
    // runs for the same reason: losing what they said to a slow or failed model call would be the
    // worst possible failure of Law 6.
    setSession((s) => s && { ...s, turns: [...s.turns, operatorTurn(text)] });

    let reply = '';
    // Annotated, not inferred: it is assigned only inside the stream callback below, so `= null`
    // alone would make it an evolving `any` and the id we navigate on would be untyped.
    let created: string | null = null;
    try {
      await sendInterviewMessage(sessionId, text, (e) => {
        if (e.event === 'chunk') {
          reply += (JSON.parse(e.data) as { text: string }).text;
          setStreaming(reply);
        } else if (e.event === 'done') {
          created = (JSON.parse(e.data) as { createdProjectId: string | null }).createdProjectId;
        }
      });
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      // The streamed text becomes a turn in the same breath as the streaming line disappears, so the
      // reply never blinks out between the last chunk and the re-read.
      if (reply) {
        setSession((s) => s && { ...s, turns: [...s.turns, agentTurn(reply)] });
        // Announce that the turn landed — not what it said. The reply's own words are already
        // sitting in the transcript bubble above; duplicating them into a live region is exactly
        // the "re-read a growing body" failure mode the streaming bubble was rejected for.
        setTurnAnnouncement('Reply received.');
      }
      setStreaming(null);
      setSending(false);
    }

    // Re-read rather than patching local state: the agent's TOOLS mutated the session while the turn
    // ran (findings, components, attachments), and only the server knows what they wrote.
    const refreshed = await refresh(sessionId);
    // The done frame is authoritative for "a project was created"; the record is the fallback for a
    // stream that died after the tool ran.
    const createdProjectId = created ?? refreshed?.createdProjectId ?? null;
    if (createdProjectId) {
      // No localStorage "recents" to update: the projects list now reads GET /projects (the record),
      // so a just-created project appears there on its own. Just go to it.
      navigate(`/p/${createdProjectId}/intake`);
    }
  }

  async function attach(files: FileList | File[] | null) {
    const list = files ? Array.from(files) : [];
    if (!sessionId || list.length === 0 || uploading) return;
    setUploading(true);
    try {
      // No optimistic chip: a file appears only once the server has stored it and tried to extract
      // it, because the chip reports the EXTRACTION result — and a chip that appeared instantly and
      // then changed status would have shown the operator a state the record never had.
      for (const file of list) await uploadAttachment(sessionId, file);
      await refresh(sessionId);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setUploading(false);
      if (fileInput.current) fileInput.current.value = '';
    }
  }

  function onDrop(e: DragEvent<HTMLDivElement>) {
    e.preventDefault();
    // A drop fires `dragenter` without a matching `dragleave`, so the depth counter is reset here
    // rather than decremented — otherwise it would drift positive across repeated drags and the
    // zone would need TWO leaves to notice the pointer is gone.
    dragDepth.current = 0;
    setDragging(false);
    void attach(e.dataTransfer?.files ?? null);
  }

  function onDragEnter(e: DragEvent<HTMLDivElement>) {
    e.preventDefault();
    dragDepth.current += 1;
    setDragging(true);
  }

  function onDragLeave() {
    dragDepth.current = Math.max(0, dragDepth.current - 1);
    if (dragDepth.current === 0) setDragging(false);
  }

  // One guard for both the Send button and the Ctrl+Enter shortcut, so the two ways to send a
  // message can't drift into disagreeing about when sending is allowed.
  const canSend = draft.trim().length > 0 && !sending;
  const cov = coverage(session?.dossier ?? [], questions);
  const blocker = session ? createBlocker(session, questions) : 'the interview has not loaded yet.';

  return (
    <div className="chatpage">
      {/* The interview has no context bar to carry a way out, and it is the one screen an
          operator can land on with nowhere obvious to go. The session is persisted, so leaving
          costs nothing. */}
      <nav className="small muted" style={{ marginBottom: 8 }}>
        <BackLink fallback="/" fallbackLabel="projects" />
      </nav>

      <div className="cap">
        <b>New project</b>
        Tell the agent about the job. It asks the rest.
      </div>

      {error && (
        <div className="banner danger" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div style={{ flex: 1 }}>{error}</div>
          {/* Nothing else ever clears `error`, so a stale banner from ten minutes ago and one from
              the turn just now render identically. The operator must be able to clear it once they
              have read it, or there is no way to tell "current" from "already dealt with". */}
          <button className="btn" type="button" onClick={() => setError(null)}>
            Dismiss
          </button>
        </div>
      )}

      {session && (
        <div className="chat">
          {/* sr-only: the visible transcript already shows the landed reply, so this exists purely
              for the screen-reader operator who was just told (via "Agent working…") that a turn
              had STARTED and, without this, would never be told it finished. See the state's
              declaration above for why it is safe from both the ticker problem and the
              re-announce-the-body problem. */}
          <span className="sr-only" role="status" aria-live="polite">
            {turnAnnouncement}
          </span>
          <div className="chat__thread" ref={scroller.ref} onScroll={scroller.onScroll}>
            {session.turns.length === 0 && streaming === null && (
              <div className="chat__empty">
                Start with the client and the job. Drop any file you already have.
              </div>
            )}

            {session.turns.map((turn, i) => (
              <div key={i}>
                <div className={`msg ${turn.role === 'agent' ? 'msg--agent' : 'msg--operator'}`}>
                  {turn.text}
                </div>
                {/* What the agent's tools DID this turn. The dossier is written by tool call, so this
                    is the audit trail of how a recorded answer got there. */}
                {turn.toolCalls.length > 0 && (
                  <div style={{ borderLeft: '2px solid var(--border)', paddingLeft: 12, margin: '2px 0 8px' }}>
                    {turn.toolCalls.map((tc, j) => (
                      <div className="step" key={j}>
                        <i className="ti ti-tool" aria-hidden="true" />
                        <div>{tc}</div>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            ))}

            {/*
              Deliberately NOT a live region. `streaming` is appended to on every chunk (see the
              `send` function above), so this text mutates many times a second while a reply is
              coming in. A screen reader that treats each mutation as a fresh announcement of the
              region's whole content — which several do, since the update isn't a text-node diff,
              it's a full re-render of the child — would re-read the ENTIRE accumulated reply after
              every single token, which is far worse than the silence it would replace: not just
              noisy but actually unable to keep up, so the operator would hear a stale partial
              sentence restart over and over rather than ever hearing the finished one. The
              "Agent working…" status below is the live announcement for this state; once the turn
              lands, its own text sits in the transcript, calmly readable rather than shouted
              mid-arrival, and the sr-only beacon further down says, once, that it arrived.
            */}
            {streaming !== null && <div className="msg msg--agent">{streaming}</div>}

            {sending && streaming === null && (
              <div className="tiny muted" role="status" aria-live="polite">
                <i className="ti ti-loader" data-running="" aria-hidden="true" /> Agent working…
              </div>
            )}
          </div>

          {session.attachments.length > 0 && (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 12 }}>
              {session.attachments.map((a) => (
                <AttachmentChip key={a.fileId} attachment={a} />
              ))}
            </div>
          )}

          {/* The composer IS the box — the textarea inside it is borderless. It doubles as the
              drop target, which is why the drag handlers and the hint live here. */}
          <div
            className="composer dropzone"
            data-testid="interview-dropzone"
            data-dragging={dragging ? 'true' : 'false'}
            onDrop={onDrop}
            onDragOver={(e) => e.preventDefault()}
            onDragEnter={onDragEnter}
            onDragLeave={onDragLeave}
          >
            {dragging && (
              <div className="dropzone__hint" aria-hidden="true">
                <i className="ti ti-file-download" /> Drop to hand it over
              </div>
            )}
            <textarea
              ref={composer}
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={(e) => {
                if (e.key !== 'Enter') return;
                // Enter sends; Cmd/Ctrl+Enter (and Shift+Enter) break the line. This is the chat
                // convention, and the box reads as a chat.
                if (e.shiftKey) return; // the browser's own newline
                if (e.metaKey || e.ctrlKey) {
                  // A modified Enter is inert in a textarea — no newline is inserted for us — so
                  // splice one in at the caret and put the caret after it.
                  e.preventDefault();
                  const at = e.currentTarget.selectionStart;
                  setDraft(`${draft.slice(0, at)}\n${draft.slice(e.currentTarget.selectionEnd)}`);
                  caret.current = at + 1;
                  return;
                }
                e.preventDefault();
                if (canSend) void send(draft);
              }}
              placeholder="Message the agent…"
              aria-label="Message the interview agent"
              rows={2}
              disabled={sending}
            />
            <div className="composer__row">
              <label className="composer__attach">
                <i className="ti ti-paperclip" aria-hidden="true" /> Attach
                <input
                  ref={fileInput}
                  type="file"
                  multiple
                  onChange={(e) => void attach(e.target.files)}
                  disabled={uploading}
                  style={{ display: 'none' }}
                />
              </label>
              {uploading && (
                <span className="tiny muted" role="status" aria-live="polite">
                  Storing and reading the file…
                </span>
              )}
              <button
                className="composer__send"
                type="button"
                aria-label="Send"
                title="Send (Enter) — Ctrl/Cmd or Shift + Enter for a new line"
                disabled={!canSend}
                onClick={() => void send(draft)}
              >
                <i className="ti ti-arrow-up" aria-hidden="true" />
              </button>
            </div>
          </div>

          {/* Coverage, as ONE line, and the create action beside it. The catalogue is never
              PRESENTED as a checklist: the operator came here to avoid a form, and a visible list
              of fields is a form with extra steps. It opens only when they ask what is missing. */}
          <div className="chat__foot">
            <span className="tiny muted" role="status" aria-live="polite">
              {cov.covered} of {cov.total} covered
            </span>
            {cov.open.length > 0 && (
              <button className="composer__attach" type="button" onClick={() => setOpenShown((v) => !v)}>
                {openShown ? 'Hide what’s open' : 'See what’s open'}
              </button>
            )}
            {/*
              This button does NOT post to a create endpoint — it asks the agent, in the
              conversation, to call its own create_project tool. There is no create_project HTTP
              endpoint, because creation is the agent's tool and a second path to it would be a way
              to create a project the gate never saw.
            */}
            <button
              className="btn primary"
              type="button"
              style={{ marginLeft: 'auto' }}
              title="The agent calls its own create_project tool — it needs the summary and the component breakdown first"
              disabled={blocker !== null || sending}
              onClick={() => void send('Everything looks right. Please create the project now.')}
            >
              Create the project
            </button>
          </div>

          {blocker && (
            <div className="tiny" style={{ color: 'var(--text-warning)', marginTop: 6 }}>
              <i className="ti ti-lock" aria-hidden="true" /> {blocker}
            </div>
          )}

          {openShown && cov.open.length > 0 && (
            <div className="chat__open">
              {cov.open.map((q) => (
                <div className="step" key={q.id}>
                  <i className="ti ti-help-circle" aria-hidden="true" />
                  <div>
                    <b>{q.prompt}</b>
                    <div className="tiny muted">{q.why}</div>
                  </div>
                </div>
              ))}
            </div>
          )}

          {/* What the agent has settled on so far — read-only by rule. To change any of it the
              operator tells the agent why (Law 4); there is no field here to edit. */}
          {(session.summary.trim().length > 0 || session.proposedComponents.length > 0) && (
            <div className="chat__open">
              {session.summary.trim().length > 0 && (
                <p className="small prose" style={{ margin: '0 0 8px' }}>
                  {session.summary}
                </p>
              )}
              {session.proposedComponents.map((c) => (
                <div className="step" key={c.id}>
                  <i className="ti ti-box" aria-hidden="true" />
                  <div>
                    <b className="data">{c.id}</b> — {c.material} · {c.application} · {c.objective}
                    {c.physicalState && <> · {c.physicalState}</>}
                    <div className="tiny muted">{c.markets.join(', ')}</div>
                  </div>
                </div>
              ))}
            </div>
          )}

        </div>
      )}

      {!session && !error && (
        <div className="tiny muted">
          <i className="ti ti-loader" data-running="" aria-hidden="true" /> Opening the interview…
        </div>
      )}
    </div>
  );
}

const operatorTurn = (text: string): InterviewTurn => ({
  role: 'operator',
  text,
  toolCalls: [],
  createdAt: new Date().toISOString(),
});

const agentTurn = (text: string): InterviewTurn => ({
  role: 'agent',
  text,
  toolCalls: [],
  createdAt: new Date().toISOString(),
});

/**
 * Take the record, but never let it DELETE a turn that is already on screen.
 *
 * The re-read exists to pick up what the agent's tools wrote, and for that the record is authoritative.
 * It is not authoritative about what was *said*: the operator's message and the reply they just watched
 * arrive are both facts, and a session read that has not yet caught up with the write must not be able
 * to erase them (Law 6 — what was said survives).
 */
function adopt(current: IntakeSession | null, refreshed: IntakeSession): IntakeSession {
  if (!current) return refreshed;
  return refreshed.turns.length >= current.turns.length
    ? refreshed
    : { ...refreshed, turns: current.turns };
}
