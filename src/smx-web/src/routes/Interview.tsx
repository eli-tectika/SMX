import { useEffect, useRef, useState, type DragEvent } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
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
  const [questions, setQuestions] = useState<IntakeQuestion[]>([]);
  const [session, setSession] = useState<IntakeSession | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const [streaming, setStreaming] = useState<string | null>(null); // the agent turn being received
  const [sending, setSending] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [openShown, setOpenShown] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  // `streaming` is a dep so the view follows the reply as it arrives token by token, not only
  // once the finished turn lands. `sending` looks redundant next to `turns.length` — React 18
  // batches `setSending(true)` with the optimistic operator turn, so `turns.length` has usually
  // already moved in that commit — but it is the ONLY dep that changes on the empty-reply failure
  // path: the stream produces no text, so no turn is ever appended, and the sole visible change is
  // `sending` flipping back to false as the "agent is thinking…" line disappears. Without it here,
  // that commit shrinks the transcript with nothing re-measuring the scroll position.
  const scroller = useStickToBottom<HTMLDivElement>([session?.turns.length, streaming, sending]);

  // No sessionId in the URL: mint one and put it there. The id lives in the URL, not in component
  // state, precisely so a reload, a bookmark or a closed tab all resume the SAME interview — Law 6.
  // `replace` so Back does not walk into a /new that mints a second session.
  useEffect(() => {
    if (sessionId) return;
    let cancelled = false;
    createIntakeSession()
      .then(({ sessionId: id }) => {
        if (!cancelled) navigate(`/new/${id}`, { replace: true });
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [sessionId, navigate]);

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
      if (reply) setSession((s) => s && { ...s, turns: [...s.turns, agentTurn(reply)] });
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
    void attach(e.dataTransfer?.files ?? null);
  }

  const cov = coverage(session?.dossier ?? [], questions);
  const blocker = session ? createBlocker(session, questions) : 'the interview has not loaded yet.';

  return (
    <>
      <div className="cap">
        <b>New project</b>
        An interview, not a form. Tell the agent about the job; it asks the rest, records what you say,
        and creates the project itself when it has enough.
      </div>

      {error && (
        <div className="banner danger" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>{error}</div>
        </div>
      )}

      {session && (
        <>
          <div
            className="region convo"
            style={{ marginBottom: 12 }}
            ref={scroller.ref}
            onScroll={scroller.onScroll}
          >
            {session.turns.length === 0 && streaming === null && (
              <div className="tiny muted">
                Nothing said yet. Start with who the client is and what the job is — and drop any file
                you already have: a brief, a lab report, an email thread.
              </div>
            )}

            {session.turns.map((turn, i) => (
              <div key={i}>
                <div className={`bub ${turn.role === 'agent' ? 'ba' : 'bu'}`}>{turn.text}</div>
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

            {streaming !== null && <div className="bub ba">{streaming}</div>}

            {sending && streaming === null && (
              <div className="tiny muted">
                <i className="ti ti-loader" data-running="" aria-hidden="true" /> The agent is thinking…
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

          <div
            className="region"
            style={{ marginBottom: 12 }}
            onDrop={onDrop}
            onDragOver={(e) => e.preventDefault()}
          >
            <textarea
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              placeholder="Talk to the agent… (drop a file here to hand it over)"
              aria-label="Message the interview agent"
              rows={3}
              disabled={sending}
              style={{ width: '100%', resize: 'vertical' }}
            />
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 8 }}>
              <label className="btn" style={{ cursor: 'pointer' }}>
                <i className="ti ti-paperclip" aria-hidden="true" /> Attach a file
                <input
                  ref={fileInput}
                  type="file"
                  multiple
                  onChange={(e) => void attach(e.target.files)}
                  disabled={uploading}
                  style={{ display: 'none' }}
                />
              </label>
              {uploading && <span className="tiny muted">Storing and reading the file…</span>}
              <button
                className="btn primary"
                type="button"
                style={{ marginLeft: 'auto' }}
                disabled={sending || !draft.trim()}
                onClick={() => void send(draft)}
              >
                Send
              </button>
            </div>
          </div>

          {/* Coverage, as ONE line. The catalogue is never PRESENTED as a checklist: the operator came
              here to avoid a form, and a visible list of fields is a form with extra steps. It opens
              only when they ask what is still missing. */}
          <div className="region" style={{ marginBottom: 12 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
              <span className="tiny muted">
                {cov.covered} of {cov.total} covered
              </span>
              {cov.open.length > 0 && (
                <button className="btn" type="button" onClick={() => setOpenShown((v) => !v)}>
                  {openShown ? 'Hide what’s open' : 'See what’s open'}
                </button>
              )}
            </div>

            {openShown && cov.open.length > 0 && (
              <div style={{ marginTop: 10 }}>
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
          </div>

          {/* What the agent has settled on so far — read-only by rule. To change any of it the
              operator tells the agent why (Law 4); there is no field here to edit. */}
          {(session.summary.trim().length > 0 || session.proposedComponents.length > 0) && (
            <div className="region" style={{ marginBottom: 12 }}>
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

          <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            {/*
              This button does NOT post to a create endpoint — it asks the agent, in the conversation,
              to call its own create_project tool. There is no create_project HTTP endpoint, because
              creation is the agent's tool and a second path to it would be a way to create a project
              the gate never saw.
            */}
            <button
              className="btn primary"
              type="button"
              disabled={blocker !== null || sending}
              onClick={() =>
                void send('Everything looks right. Please create the project now.')
              }
            >
              Create the project
            </button>
            <span className="tiny muted">
              Creating the project is the agent’s own tool — it needs the summary and the component
              breakdown before it can call it.
            </span>
            {blocker && (
              <div className="tiny" style={{ color: 'var(--text-warning)', flexBasis: '100%' }}>
                <i className="ti ti-lock" aria-hidden="true" /> {blocker}
              </div>
            )}
          </div>
        </>
      )}

      {!session && !error && (
        <div className="tiny muted">
          <i className="ti ti-loader" data-running="" aria-hidden="true" /> Opening the interview…
        </div>
      )}
    </>
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
