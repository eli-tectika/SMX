import { useCallback, useEffect, useState } from 'react';
import { NotFound, getAmendments, getIntakeBrief, getVpGate } from '../../api/client';
import type { Amendment, ComponentSpec, IntakeBrief as Brief, VpGate } from '../../api/types';
import { IntakeBrief } from '../../components/IntakeBrief';
import { Data } from '../../components/ui/Data';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { handOffToChat } from '../../domain/chatHandoff';
import type { ScreenProps } from '../ProjectLayout';

const obj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null;

/**
 * Enough of a brief to render one: the three fields `IntakeBrief` walks without asking first.
 *
 * Not a schema check — the point is only that nothing here can throw out of a render. A brief with
 * an unexpected extra field is fine; a brief whose `summary` is undefined is not.
 */
const readableBrief = (b: unknown): b is Brief =>
  obj(b) &&
  typeof b.summary === 'string' &&
  Array.isArray(b.dossier) &&
  Array.isArray(b.attachments) &&
  Array.isArray(b.transcript);

/**
 * Overview — the project's own page, and the home of everything that has no phase.
 *
 * Intake is not a phase any more (spec §4): it runs during project creation, transcribing the brief
 * the operator just dictated in the interview, and a step the operator never visits is not a step.
 * What it produced still has to be readable, so the brief lives here, together with the two things
 * that belong to the project rather than to a phase — the amendment log, and which signatures are
 * outstanding.
 *
 * AMENDING IS A CONVERSATION, NOT A FORM (spec §16.2). This screen used to carry a picker, a value
 * box and a reason box: an operator typing into fields with the reason demoted to one more field,
 * which is precisely the direct edit Law 4 forbids. The form is deleted. The intake agent has the
 * right-hand panel on this screen (components/shell/projectNav.ts), the operator says what changed
 * and why — *"the customer confirmed they're shipping to Japan as well"* — and the agent calls the
 * amendment tool. What stays on the screen is the RECORD of it: the log, and the signatures an
 * amendment would cost.
 *
 * THE TRAP THIS SCREEN IS BUILT AGAINST: `done` no longer means signed. The pipeline runs end to end
 * with nobody's signature on anything, so a project whose every stage reads `done` can still have
 * both gates unsigned, the compliance package refused and every order refused. This screen therefore
 * reads the GATE RECORDS and never infers a signature from stage statuses.
 */
export function Overview({ project }: ScreenProps) {
  const [brief, setBrief] = useState<Brief | null>(null);
  const [briefState, setBriefState] = useState<'loading' | 'ready' | 'none' | 'error'>('loading');
  const [briefError, setBriefError] = useState<string>();
  const [gates, setGates] = useState<GateRead>({ kind: 'loading' });

  useEffect(() => {
    let cancelled = false;
    setBriefState('loading');
    getIntakeBrief(project.projectId)
      .then((r) => {
        if (cancelled) return;
        if (r === NotFound) {
          setBrief(null);
          setBriefState('none');
        } else if (!readableBrief(r)) {
          /*
           * THE SCREEN GUARDS ITS OWN PAYLOAD, and the direction it leans matters. `client.ts` casts
           * every response with `as` and validates nothing, so a drifted or list-shaped body arrives
           * here typed as a brief and `IntakeBrief` reaches straight into `summary.trim()`. That
           * throw is caught by StageErrorBoundary — the backstop, not the plan — and costs the
           * operator the whole screen over one bad read. An unreadable brief is a FAILED read, said
           * plainly; it is never an absent one, because "created through the API" is a different
           * claim about the project entirely.
           */
          setBriefError('the interview brief came back in a shape this screen cannot read');
          setBriefState('error');
        } else {
          setBrief(r);
          setBriefState('ready');
        }
      })
      .catch((e) => {
        if (cancelled) return;
        setBriefError(e instanceof Error ? e.message : String(e));
        setBriefState('error');
      });
    return () => {
      cancelled = true;
    };
  }, [project.projectId]);

  /*
   * ONE gate read, shared. Two consumers need it — what is outstanding, and what an amendment would
   * void — and two reads of one record could disagree with each other on screen at the same time.
   *
   * ONE GATE, and there used to be two: the regulatory gate is removed rather than demoted (§16.4),
   * so the VP determination is the only signature left and the only one an amendment can void.
   */
  useEffect(() => {
    let cancelled = false;
    getVpGate(project.projectId)
      .then((vp) => {
        if (!cancelled) setGates({ kind: 'ready', vp });
      })
      .catch((e) => {
        if (!cancelled)
          setGates({ kind: 'error', message: e instanceof Error ? e.message : String(e) });
      });
    return () => {
      cancelled = true;
    };
  }, [project.projectId]);

  const payload = project.payload;
  /*
   * The record's own component list is preferred over the brief's copy: it carries more (physical
   * state, batch mass). The brief is the fallback for a project whose payload has not loaded, so the
   * screen is never blank about its own subject.
   */
  const components: ComponentSpec[] = payload?.components ?? brief?.components ?? [];

  return (
    <>
      <section className="screen">
        <SectionHeader title="What we're marking" headingLevel={3} count={components.length} />

        {briefState === 'error' && (
          <div className="banner danger" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>Could not load the interview brief: {briefError}</div>
          </div>
        )}

        {/* Read-only by law (Law 4): nothing the agent wrote is hand-editable, here or anywhere. */}
        {brief && <IntakeBrief brief={brief} />}

        {briefState === 'none' && (
          <p className="small secondary" style={{ margin: '0 0 12px' }}>
            No interview brief on the record — this project was created through the API.
          </p>
        )}

        {components.length > 0 && (
          <table className="mx">
            <thead>
              <tr>
                <th>Component</th>
                <th>Material</th>
                <th>Application</th>
                <th>Markets</th>
                <th>Objective</th>
                <th>State</th>
                <th>Batch mass</th>
              </tr>
            </thead>
            <tbody>
              {components.map((c) => (
                <tr key={c.id}>
                  <td>
                    <Data kind="id">{c.id}</Data>
                  </td>
                  <td>{c.material}</td>
                  <td>{c.application}</td>
                  <td>{Array.isArray(c.markets) ? c.markets.join(', ') : ''}</td>
                  <td>{c.objective}</td>
                  <td>{c.physicalState ? c.physicalState : <span className="muted">—</span>}</td>
                  {/* Absent is not zero. ppm is mg/kg, so a missing batch mass yields no order amount
                      at all — say "not given", never render a 0 the operator could act on. */}
                  <td>
                    {c.batchMassKg === undefined ? (
                      <span className="muted">not given</span>
                    ) : (
                      <Data kind="num">{`${c.batchMassKg} kg`}</Data>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {/* A client's exclusions, in mono because they are element symbols — and in no colour,
            because they are an input constraint the client handed us, not a verdict this system
            reached. Red here would say "Fail", which is a claim nothing has made yet. */}
        {payload && payload.clientRestrictedList.length > 0 && (
          <p className="small secondary" style={{ margin: '12px 0 0' }}>
            Ruled out by the client:{' '}
            {payload.clientRestrictedList.map((e, i) => (
              <span key={e}>
                {i > 0 ? ' · ' : ''}
                <Data kind="element">{e}</Data>
              </span>
            ))}
          </p>
        )}
      </section>

      <Outstanding project={project} gates={gates} />

      <Amendments projectId={project.projectId} gates={gates} />
    </>
  );
}

/* ---------------------------------------------------------------------------
   What is outstanding.
   --------------------------------------------------------------------------- */

type GateRead =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; vp: VpGate };

/** Anything that is not exactly "approved" is UNSIGNED — an unrecognised status included. */
const isSigned = (status: string | undefined) => status === 'approved';

/**
 * The one signature left, and what it releases.
 *
 * This is the whole answer to "am I finished", and it is read from the gate record because nothing
 * else can answer it: an unattended run ends `complete, unsigned`, which looks identical to finished
 * on every stage status in the record. The signature is named WITH the irreversible act it gates
 * (spec §10) — a signature described without its consequence is a chore rather than a decision.
 *
 * A failed gate read is stated as unknown, never as unsigned and never as signed. Both wrong answers
 * are costly in opposite directions, and neither is available from a request that did not answer.
 */
function Outstanding({ project, gates }: { project: ScreenProps['project']; gates: GateRead }) {
  return (
    <section className="screen">
      <SectionHeader title="What is outstanding" headingLevel={3} />

      {project.analysisStartedAt === null && (
        <div className="banner warn" role="status">
          <i className="ti ti-player-play" aria-hidden="true" />
          <div className="prose">
            <b>The analysis has not been started.</b> Nothing past intake will run until you start it.
          </div>
        </div>
      )}

      {gates.kind === 'loading' && (
        <p className="small muted" style={{ margin: 0 }}>
          <i className="ti ti-loader" data-running="" aria-hidden="true" /> Reading the gates…
        </p>
      )}

      {gates.kind === 'error' && (
        <div className="banner danger" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div className="prose">
            <b>The determination could not be read.</b> This screen cannot say whether it is
            outstanding — not that it is not. {gates.message}
          </div>
        </div>
      )}

      {gates.kind === 'ready' && (
        <ul style={{ listStyle: 'none', margin: 0, padding: 0 }}>
          <SignatureRow
            label="VP R&D determination"
            signed={isSigned(gates.vp.status)}
            releases="procurement — and each order is still held behind its safety sheet"
            blockers={Array.isArray(gates.vp.blockers) ? gates.vp.blockers.length : 0}
          />
        </ul>
      )}
    </section>
  );
}

function SignatureRow({
  label,
  signed,
  releases,
  blockers,
}: {
  label: string;
  signed: boolean;
  releases: string;
  blockers: number;
}) {
  return (
    <li
      data-signature={signed ? 'signed' : 'outstanding'}
      style={{
        display: 'flex',
        alignItems: 'flex-start',
        gap: 'var(--s2)',
        padding: 'var(--s2) 0',
        borderTop: '1px solid var(--border)',
      }}
    >
      <i
        className={`ti ${signed ? 'ti-writing-sign' : 'ti-signature'}`}
        role="img"
        aria-label={signed ? 'signed' : 'outstanding'}
        style={{ color: signed ? 'var(--text-teal)' : 'var(--text-warning)', marginTop: 2 }}
      />
      <span style={{ flex: 1, minWidth: 0 }}>
        <span className="small" style={{ fontWeight: 500 }}>
          {label} — {signed ? 'signed' : 'not signed'}
        </span>
        <span className="small secondary" style={{ display: 'block' }}>
          {signed ? 'Released: ' : 'Withholds: '}
          {releases}.
          {!signed && blockers > 0 ? ` ${blockers} condition${blockers === 1 ? '' : 's'} outstanding.` : ''}
        </span>
      </span>
    </li>
  );
}

/* ---------------------------------------------------------------------------
   Amendments (spec §9, §16.2).
   --------------------------------------------------------------------------- */

/**
 * The amendment log, and the one thing an amendment can cost that the log cannot show in advance.
 *
 * There is no form here. The composer in the right-hand panel IS the amendment surface: the operator
 * states what changed and why, the intake agent validates the field through `IntakeAnswers`' allow-list
 * (so the physicist's measured data stays unwritable by construction), patches the constraints, and
 * resets the stages in the blast radius. Law 4 holds because the operator never hand-mutates the
 * record — they tell the agent what changed, and the reason is what the log keeps.
 */
function Amendments({ projectId, gates }: { projectId: string; gates: GateRead }) {
  const [log, setLog] = useState<Amendment[]>([]);
  const [logState, setLogState] = useState<'loading' | 'ready' | 'error'>('loading');
  const [logError, setLogError] = useState<string>();

  const loadLog = useCallback(async () => {
    try {
      const entries = await getAmendments(projectId);
      // A non-list is a FAILED read, not an empty log. Cast to `[]` it would report a project that
      // has been amended six times as never amended, which is the record erasing its own history.
      if (!Array.isArray(entries)) throw new Error('the amendment log came back in an unreadable shape');
      setLog(entries);
      setLogState('ready');
    } catch (e) {
      setLogError(e instanceof Error ? e.message : String(e));
      setLogState('error');
    }
  }, [projectId]);

  useEffect(() => {
    void loadLog();
  }, [loadLog]);

  return (
    <section className="screen">
      <SectionHeader
        title="Amendments"
        headingLevel={3}
        count={logState === 'ready' ? log.length : undefined}
      />

      <SignatureVoidNotice gates={gates} />

      {logState === 'error' && (
        <div className="banner warn" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The amendment log could not be read.</b> This is not an empty log — it is an unknown
            one. {logError}
          </div>
        </div>
      )}

      {logState === 'ready' && log.length === 0 && (
        <EmptyState icon="ti-history" title="No amendments." />
      )}

      {logState === 'ready' && log.length > 0 && (
        <table className="mx">
          <thead>
            <tr>
              <th>When</th>
              <th>Requirement</th>
              <th>From</th>
              <th>To</th>
              <th>Reason</th>
              <th>Re-ran</th>
            </tr>
          </thead>
          <tbody>
            {log.map((a, i) => (
              <tr key={`${a.at}|${a.field}|${i}`}>
                <td>
                  <Data kind="date">{typeof a.at === 'string' ? a.at.slice(0, 10) : '—'}</Data>
                </td>
                <td>
                  {a.field}
                  {a.componentId ? (
                    <div className="tiny muted">
                      <Data kind="id">{a.componentId}</Data>
                    </div>
                  ) : null}
                </td>
                <td className="secondary">{a.from || <span className="muted">not set</span>}</td>
                <td>{a.to}</td>
                <td className="secondary">{a.reason}</td>
                <td className="tiny">
                  {(Array.isArray(a.rerun) ? a.rerun : []).join(', ')}
                  {/*
                    What the amendment ACTUALLY cost that day, recorded rather than re-derived. A
                    voided signature that left no trace afterwards is indistinguishable from one
                    that was never given.
                  */}
                  {Array.isArray(a.voidedSignatures) && a.voidedSignatures.length > 0 && (
                    <div style={{ color: 'var(--text-warning)' }}>
                      voided: {a.voidedSignatures.join(', ')}
                    </div>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

/**
 * THE ONE PLACE EXECUTION STOPS TO ASK (spec §9.4), moved to where the operator can see it BEFORE
 * they speak rather than as a toast after.
 *
 * Nothing waits in this system any more — except that silently un-signing a human's approval is not
 * something software does quietly. The endpoint still answers 409 with the signatures an amendment
 * would void, and the intake agent still has to be told to proceed anyway. What this block does is
 * make that refusal predictable: it names, from the GATE RECORDS, exactly which signatures are on
 * file right now, so the 409 is never the first the operator hears of it.
 *
 * It renders only when there IS a signature to lose. On an unsigned project it is silent — a standing
 * warning about a thing that cannot happen is the noise that teaches operators to ignore warnings.
 *
 * The confirm is a SECOND act and it belongs in the conversation: the operator tells the agent to
 * amend anyway, and the reason travels with it. A button here that POSTed `confirmSignatureVoid` would
 * be the form coming back through the side door, voiding a human signature with no reason attached.
 */
function SignatureVoidNotice({ gates }: { gates: GateRead }) {
  const [draft, setDraft] = useState<string | null>(null);

  if (gates.kind !== 'ready') return null;

  if (!isSigned(gates.vp.status)) return null;
  const signed = ["VP R&D's determination"];

  const sentence = `Amend a requirement even though it voids ${signed.join(' and ')} — `;

  return (
    <div className="banner warn" role="status" data-void-warning="">
      <i className="ti ti-signature" aria-hidden="true" />
      <div className="prose">
        <b>
          {signed.length === 1 ? 'A signature is' : 'Two signatures are'} already on file:{' '}
          {signed.join(' and ')}.
        </b>{' '}
        An amendment whose re-run reaches {signed.length === 1 ? 'it' : 'them'} voids{' '}
        {signed.length === 1 ? 'it' : 'them'}, and the agent will ask before it does.
        <div style={{ marginTop: 'var(--s2)' }}>
          <button
            type="button"
            className="btn"
            onClick={() => setDraft(handOffToChat(sentence) ? null : sentence)}
          >
            <i className="ti ti-message-2" aria-hidden="true" /> Tell the agent — say what changed and
            why
          </button>
        </div>
        {draft && (
          <p className="small secondary" style={{ margin: '6px 0 0' }}>
            The agent column is closed. Open it and start with: <span className="data">{draft}</span>
          </p>
        )}
      </div>
    </div>
  );
}
