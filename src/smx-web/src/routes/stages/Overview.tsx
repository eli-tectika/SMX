import { useCallback, useEffect, useState } from 'react';
import {
  NotFound,
  getAmendments,
  getIntakeBrief,
  getRegulatoryGate,
  getVpGate,
  postAmendment,
} from '../../api/client';
import type {
  Amendment,
  AmendmentConflict,
  ComponentSpec,
  IntakeBrief as Brief,
  RegulatoryGate,
  VpGate,
} from '../../api/types';
import { IntakeBrief } from '../../components/IntakeBrief';
import { Data } from '../../components/ui/Data';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import type { ScreenProps } from '../ProjectLayout';

/**
 * Overview — the project's own page, and the home of everything that has no phase.
 *
 * Intake is not a phase any more (spec §4): it runs during project creation, transcribing the brief
 * the operator just dictated in the interview, and a step the operator never visits is not a step.
 * What it produced still has to be readable, so the brief lives here. Two other things live here for
 * the same reason — they belong to the project rather than to a phase:
 *
 *   - **Amendments** (§9). A requirement changed on the customer's side. That is not a revision of an
 *     agent's output and not an intake gap-fill; it is a new fact upstream of every agent, and it
 *     re-runs a declared SET of stages. Law 4 survives intact: the operator does not hand-edit the
 *     record, they state what changed and why, and the reason is recorded.
 *   - **What is outstanding.** The two signatures, read from the gate records.
 *
 * THE TRAP THIS SCREEN IS BUILT AGAINST: `done` no longer means signed. The pipeline runs end to end
 * with nobody's signature on anything, so a project whose every stage reads `done` can still have
 * both gates unsigned, the compliance package refused and every order refused. This screen therefore
 * reads the GATE RECORDS and never infers a signature from stage statuses.
 */
export function Overview({ project, refreshProject }: ScreenProps) {
  const [brief, setBrief] = useState<Brief | null>(null);
  const [briefState, setBriefState] = useState<'loading' | 'ready' | 'none' | 'error'>('loading');
  const [briefError, setBriefError] = useState<string>();

  useEffect(() => {
    let cancelled = false;
    setBriefState('loading');
    getIntakeBrief(project.projectId)
      .then((r) => {
        if (cancelled) return;
        if (r === NotFound) {
          setBrief(null);
          setBriefState('none');
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
        <SectionHeader
          title="What we're marking"
          headingLevel={3}
          count={components.length}
          hint={payload ? undefined : 'from the interview brief — the record carries no payload'}
        />

        {briefState === 'error' && (
          <div className="banner danger" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>Could not load the interview brief: {briefError}</div>
          </div>
        )}

        {/* Read-only by law (Law 4): nothing the agent wrote is hand-editable, here or anywhere. */}
        {brief && <IntakeBrief brief={brief} />}

        {briefState === 'none' && (
          <p className="prose" style={{ margin: '0 0 12px' }}>
            This project was created through the form or the API rather than an interview, so there is
            no brief to read. What was submitted is below.
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

      <Outstanding project={project} />

      <Amendments
        projectId={project.projectId}
        components={components}
        onAmended={refreshProject}
      />
    </>
  );
}

/* ---------------------------------------------------------------------------
   What is outstanding.
   --------------------------------------------------------------------------- */

type GateRead =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; regulatory: RegulatoryGate; vp: VpGate };

/** Anything that is not exactly "approved" is UNSIGNED — an unrecognised status included. */
const isSigned = (status: string | undefined) => status === 'approved';

/**
 * The two signatures and what each one releases.
 *
 * This is the whole answer to "am I finished", and it is read from the gate records because nothing
 * else can answer it: an unattended run ends `complete, unsigned`, which looks identical to finished
 * on every stage status in the record. Each signature is named WITH the irreversible act it gates
 * (spec §10) — a signature described without its consequence is a chore rather than a decision.
 *
 * A failed gate read is stated as unknown, never as unsigned and never as signed. Both wrong answers
 * are costly in opposite directions, and neither is available from a request that did not answer.
 */
function Outstanding({ project }: { project: ScreenProps['project'] }) {
  const [state, setState] = useState<GateRead>({ kind: 'loading' });

  useEffect(() => {
    let cancelled = false;
    Promise.all([getRegulatoryGate(project.projectId), getVpGate(project.projectId)])
      .then(([regulatory, vp]) => {
        if (!cancelled) setState({ kind: 'ready', regulatory, vp });
      })
      .catch((e) => {
        if (!cancelled)
          setState({ kind: 'error', message: e instanceof Error ? e.message : String(e) });
      });
    return () => {
      cancelled = true;
    };
  }, [project.projectId]);

  return (
    <section className="screen">
      <SectionHeader
        title="What is outstanding"
        headingLevel={3}
        hint="every stage running to done is not the same as signed"
      />

      {project.analysisStartedAt === null && (
        <div className="banner warn" role="status">
          <i className="ti ti-player-play" aria-hidden="true" />
          <div className="prose">
            <b>The analysis has not been started.</b> Intake transcribed the brief above; nothing past
            it will run until you start it. The agent may create a project — only you may start one.
          </div>
        </div>
      )}

      {state.kind === 'loading' && (
        <p className="small muted" style={{ margin: 0 }}>
          <i className="ti ti-loader" data-running="" aria-hidden="true" /> Reading the gates…
        </p>
      )}

      {state.kind === 'error' && (
        <div className="banner danger" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div className="prose">
            <b>The gates could not be read.</b> This screen cannot say which signatures are
            outstanding — not that none are. {state.message}
          </div>
        </div>
      )}

      {state.kind === 'ready' && (
        <ul style={{ listStyle: 'none', margin: 0, padding: 0 }}>
          <SignatureRow
            label="Regulatory sign-off"
            signed={isSigned(state.regulatory.status)}
            releases="the compliance-package export"
            blockers={Array.isArray(state.regulatory.blockers) ? state.regulatory.blockers.length : 0}
          />
          <SignatureRow
            label="VP R&D determination"
            signed={isSigned(state.vp.status)}
            releases="procurement — and each order is still held behind its safety sheet"
            blockers={Array.isArray(state.vp.blockers) ? state.vp.blockers.length : 0}
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
   Amendments (spec §9).
   --------------------------------------------------------------------------- */

/**
 * The amendable requirements, and they are exactly `IntakeAnswers`' allow-list.
 *
 * The physicist's measured data — element pools, measured backgrounds, device LODs — is absent by
 * construction, in the domain and therefore here: `Amendments.Apply` cannot express a write to it. If
 * it is wrong the physicist must re-measure and re-enter it through the XRF form on Dosing.
 *
 * `scope` is what the operator is told the change costs BEFORE they make it. It mirrors
 * `RerunScope.Map`, which is the server's authority — this copy explains, it does not decide, and the
 * POST re-derives the real scope and reports it back in the log.
 */
const AMENDABLE: { field: string; label: string; perComponent: boolean; scope: string }[] = [
  { field: 'material', label: 'Material', perComponent: true, scope: 'Discovery, Regulatory, Dosing and Sign-off' },
  { field: 'application', label: 'Application', perComponent: true, scope: 'Regulatory and Sign-off' },
  { field: 'markets', label: 'Target markets (comma-separated)', perComponent: true, scope: 'Regulatory and Sign-off' },
  { field: 'objective', label: 'Objective', perComponent: true, scope: 'Discovery, Regulatory, Dosing and Sign-off' },
  { field: 'batchMassKg', label: 'Batch mass (kg)', perComponent: true, scope: 'Dosing and Sign-off' },
  { field: 'clientRestrictedList', label: "Client's restricted list (comma-separated)", perComponent: false, scope: 'Regulatory and Sign-off' },
];

function Amendments({
  projectId,
  components,
  onAmended,
}: {
  projectId: string;
  components: ComponentSpec[];
  onAmended: () => void;
}) {
  const [log, setLog] = useState<Amendment[]>([]);
  const [logState, setLogState] = useState<'loading' | 'ready' | 'error'>('loading');
  const [logError, setLogError] = useState<string>();

  const [field, setField] = useState(AMENDABLE[0].field);
  const [componentId, setComponentId] = useState('');
  const [value, setValue] = useState('');
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  /** The 409 body: which signatures this amendment would void, and what it would re-run. */
  const [conflict, setConflict] = useState<AmendmentConflict | null>(null);

  const spec = AMENDABLE.find((a) => a.field === field) ?? AMENDABLE[0];

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

  useEffect(() => {
    if (componentId === '' && components.length > 0) setComponentId(components[0].id);
  }, [components, componentId]);

  const submit = useCallback(
    async (confirmSignatureVoid: boolean) => {
      setBusy(true);
      setError(null);
      try {
        const res = await postAmendment(projectId, {
          field,
          value,
          reason,
          componentId: spec.perComponent ? componentId : undefined,
          confirmSignatureVoid,
        });
        if (!res.ok) {
          // NOT an error — the system asking a question. The operator is owed the list of what they
          // are about to un-sign before they are offered the button that does it.
          setConflict(res.conflict);
          return;
        }
        setConflict(null);
        setValue('');
        setReason('');
        await loadLog();
        // The amendment reset stages to `pending` and may have voided a gate. The shell reads the
        // project, so without this it goes on describing the analysis the amendment invalidated.
        onAmended();
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e));
      } finally {
        setBusy(false);
      }
    },
    [projectId, field, value, reason, componentId, spec.perComponent, loadLog, onAmended],
  );

  const ready = value.trim().length > 0 && reason.trim().length > 0;

  return (
    <section className="screen">
      <SectionHeader
        title="Amend a requirement"
        headingLevel={3}
        hint="a fact about the customer's product changed — the agents re-derive from it"
      />

      <p className="prose" style={{ margin: '0 0 var(--s3)' }}>
        This is not an edit. You state what the requirement now <b>is</b> and <b>why</b>; the reason is
        recorded, the constraints are patched, and exactly the stages that depended on it re-run. The
        physicist's measured data is not amendable here — if the XRF is wrong, it must be re-measured
        and re-entered on Dosing.
      </p>

      <div style={{ display: 'grid', gap: 'var(--s2)', maxWidth: 620 }}>
        <label className="small">
          <span className="secondary">Requirement</span>
          <select
            value={field}
            onChange={(e) => {
              setField(e.target.value);
              setConflict(null);
            }}
            style={{ font: 'inherit', fontSize: 'var(--t-small)', padding: '4px 6px', width: '100%' }}
          >
            {AMENDABLE.map((a) => (
              <option key={a.field} value={a.field}>
                {a.label}
              </option>
            ))}
          </select>
        </label>

        {spec.perComponent && (
          <label className="small">
            <span className="secondary">Component</span>
            <select
              value={componentId}
              onChange={(e) => setComponentId(e.target.value)}
              style={{ font: 'inherit', fontSize: 'var(--t-small)', padding: '4px 6px', width: '100%' }}
            >
              {components.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.id}
                </option>
              ))}
            </select>
          </label>
        )}

        <label className="small">
          <span className="secondary">What it now is</span>
          <input
            value={value}
            onChange={(e) => setValue(e.target.value)}
            aria-label="New value"
            disabled={busy}
            style={{ font: 'inherit', fontSize: 'var(--t-small)', padding: '4px 6px', width: '100%' }}
          />
        </label>

        <label className="small">
          <span className="secondary">Why it changed</span>
          <textarea
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            rows={2}
            aria-label="Reason for the amendment"
            placeholder="What changed on the customer's side. Required — this is what the record keeps."
            disabled={busy}
            style={{
              font: 'inherit',
              fontSize: 'var(--t-small)',
              padding: '6px 8px',
              width: '100%',
              border: '0.5px solid var(--border-strong)',
              borderRadius: 'var(--r1)',
              resize: 'vertical',
            }}
          />
        </label>

        <p className="small secondary" style={{ margin: 0 }}>
          This re-runs <b>{spec.scope}</b>.
        </p>

        {/*
          The one place execution stops to ask (§9.4). Nothing waits in this system any more — except
          that silently un-signing a human's approval is not something software should do quietly. The
          confirm button is a SECOND press, and it names what it destroys.
        */}
        {conflict && (
          <div className="banner warn" role="alert">
            <i className="ti ti-signature" aria-hidden="true" />
            <div className="prose">
              <b>This amendment voids a signature already on file.</b>
              <div>
                Would be un-signed:{' '}
                {(Array.isArray(conflict.voids) ? conflict.voids : []).join(', ') || 'unnamed'}. Would
                re-run: {(Array.isArray(conflict.rerun) ? conflict.rerun : []).join(', ') || 'unnamed'}
                . The signature must then be given again.
              </div>
              <button
                type="button"
                className="btn"
                disabled={busy}
                onClick={() => void submit(true)}
                style={{ marginTop: 'var(--s2)' }}
              >
                Amend anyway and void the signature
              </button>
            </div>
          </div>
        )}

        {error && (
          <div className="banner danger" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>
              <b>The amendment was refused.</b>
              <div className="small" style={{ marginTop: 3 }}>{error}</div>
            </div>
          </div>
        )}

        <div>
          <button
            type="button"
            className="btn primary"
            disabled={!ready || busy}
            onClick={() => void submit(false)}
            title={ready ? undefined : 'Both the new value and the reason are required'}
          >
            <i className={`ti ${busy ? 'ti-loader' : 'ti-edit'}`} aria-hidden="true" />{' '}
            {busy ? 'Recording…' : 'Record the amendment'}
          </button>
        </div>
      </div>

      <SectionHeader
        eyebrow="Amendment log"
        count={logState === 'ready' ? log.length : undefined}
        hint="what changed, when, and why"
      />

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
        <EmptyState
          icon="ti-history"
          title="No amendments."
          body="Nothing about the customer's requirement has changed since intake."
        />
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
