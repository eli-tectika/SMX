import { Fragment, useCallback, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  ApiError,
  NotFound,
  getDecision,
  getDosing,
  getMsdsRegistry,
  getVpGate,
  orderSubstance,
  recordVpDetermination,
} from '../../api/client';
import type {
  ComponentDecision,
  DecisionDoc,
  DosingDoc,
  MsdsEntry,
  VpGate as VpGateState,
} from '../../api/types';
import { Loading } from '../../components/Loading';
import { StageStatusCard } from '../../components/StageStatusCard';
import { Data } from '../../components/ui/Data';
import { Gate, type Requirement } from '../../components/ui/Gate';
import { EmptyState, SectionHeader, StatCard } from '../../components/ui/Primitives';
import type { ScreenProps } from '../ProjectLayout';

const CRITERIA = ['regulatory', 'dosing', 'cost'] as const;
type Criterion = (typeof CRITERIA)[number];

/**
 * Spec §4.7 requires every row be traceable end-to-end. Each criterion is owned by the stage that
 * produced it, so "trace" is a link to that stage plus the record id the claim came from.
 */
const OWNER: Record<Criterion, { stage: string; label: string }> = {
  regulatory: { stage: 'regulatory', label: 'Regulatory gate' },
  dosing: { stage: 'dosing', label: 'Dosing & codes' },
  cost: { stage: 'cost', label: 'Cost & availability' },
};

/**
 * The VP R&D gate (spec §4.7) — the final hard gate, and the last screen of the journey.
 *
 * Approval releases procurement and writes the Marker Library and Learned Conclusions, so this is the
 * highest-consequence action in the system. Four things it exists to get right:
 *
 *  1. **Law 9, as pixels.** `proposedCode` is the agent's offer; `confirmedCode` is the VP's signature
 *     and arrives as an explicit `null` until signed. They never share a treatment. A proposal wearing
 *     the confirmed chip IS the agent signing the gate.
 *  2. **Armability is the server's word.** `GET /gate/vp` runs the same checks the POST enforces —
 *     including the two the UI cannot see (a stage no longer parked at `awaiting-VP`, a revision in
 *     flight). Tallying anything browser-side would advertise a pen the POST refuses, and a lying
 *     affordance is how a gate gets rubber-stamped.
 *  3. **MSDS is not a gate requirement.** It gates each individual ORDER (§5). The old fixture listed
 *     it among the gate's requirements, which invented a precondition the server does not enforce.
 *  4. **Release is eventually consistent.** Procurement flips to `released` by the ORCHESTRATOR
 *     reacting to the approved gate, not by the signing call. So signing re-reads rather than
 *     assuming; until the record says released, no order control exists.
 *
 * It reads as a DECISION RECORD, not a work surface, and the page order is the argument: provenance,
 * then the state of the record, then the evidence, then the signature block last. Signing after the
 * evidence rather than above it is the anti-rubber-stamping law (§1.8) expressed as layout.
 */
export function Decision({ project, refreshProject }: ScreenProps) {
  const stage = project.stages.decision;
  const status = stage?.status;

  const [doc, setDoc] = useState<DecisionDoc | null>(null);
  const [gate, setGate] = useState<VpGateState | null>(null);
  const [dosing, setDosing] = useState<DosingDoc | null>(null);
  const [sheets, setSheets] = useState<MsdsEntry[]>([]);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [errMsg, setErrMsg] = useState<string>();
  const [expanded, setExpanded] = useState<string | null>(null);
  const [choice, setChoice] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<'sign' | 'reject' | null>(null);
  const [signError, setSignError] = useState<string | null>(null);
  const [orderError, setOrderError] = useState<string | null>(null);
  const [ordering, setOrdering] = useState<string | null>(null);

  const load = useCallback(
    async (signal?: { cancelled: boolean }) => {
      try {
        const [d, g, dose, ms] = await Promise.all([
          getDecision(project.projectId),
          getVpGate(project.projectId),
          getDosing(project.projectId),
          getMsdsRegistry(),
        ]);
        if (signal?.cancelled) return;
        setGate(g);
        setDosing(dose === NotFound ? null : dose);
        setSheets(ms);
        if (d === NotFound) {
          setDoc(null);
          setPhase('absent');
        } else {
          setDoc(d);
          setPhase('ready');
        }
      } catch (err) {
        if (!signal?.cancelled) {
          setErrMsg(err instanceof Error ? err.message : String(err));
          setPhase('error');
        }
      }
    },
    [project.projectId],
  );

  useEffect(() => {
    const signal = { cancelled: false };
    void load(signal);
    return () => {
      signal.cancelled = true;
    };
  }, [load, status]);

  /** The code each component will be signed with: the agent's proposal until the VP picks another. */
  const confirmations = useMemo(
    () =>
      (doc?.components ?? []).map((c) => ({
        componentId: c.componentId,
        code: choice[c.componentId] ?? c.proposedCode?.ratioSignature ?? '',
      })),
    [doc, choice],
  );

  const sign = useCallback(
    async (note?: string) => {
      if (!note) return;
      setBusy('sign');
      setSignError(null);
      try {
        await recordVpDetermination(project.projectId, {
          determination: 'approved',
          reason: note,
          confirmations,
        });
        refreshProject();
        await load();
      } catch (err) {
        // The server re-checks and can refuse a button that looked enabled (a concurrent revise, a
        // stage that left its park). Show its words and re-read the gate for the fresh blockers.
        setSignError(err instanceof ApiError ? err.message : String(err));
        await load();
      } finally {
        setBusy(null);
      }
    },
    [project.projectId, confirmations, load, refreshProject],
  );

  const reject = useCallback(
    async (note: string) => {
      setBusy('reject');
      setSignError(null);
      try {
        await recordVpDetermination(project.projectId, {
          determination: 'rejected',
          reason: note,
        });
        refreshProject();
        await load();
      } catch (err) {
        setSignError(err instanceof ApiError ? err.message : String(err));
        await load();
      } finally {
        setBusy(null);
      }
    },
    [project.projectId, load, refreshProject],
  );

  const order = useCallback(
    async (cas: string) => {
      setOrdering(cas);
      setOrderError(null);
      try {
        await orderSubstance(project.projectId, cas);
        await load();
      } catch (err) {
        setOrderError(err instanceof ApiError ? err.message : String(err));
      } finally {
        setOrdering(null);
      }
    },
    [project.projectId, load],
  );

  if (phase === 'loading') return <Loading what="the decision record" />;

  if (phase === 'error') {
    return (
      <section className="screen">
        <div className="banner warn" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The decision record could not be read.</b>
            <div className="tiny" style={{ marginTop: 3 }}>{errMsg}</div>
          </div>
        </div>
      </section>
    );
  }

  const components = doc?.components ?? [];
  const rows = components.flatMap((c) => c.rows);
  const blocking = rows.filter((r) => CRITERIA.some((k) => !r.cleared[k]));
  const confirmed = components.filter((c) => c.confirmedCode !== null).length;
  const released = doc?.procurement.status === 'released';

  /**
   * The determination has been made — either the gate record says so, or every component carries a
   * signature (both are written by the same endpoint, and either alone is proof one ran).
   *
   * When it has, the pen is withdrawn rather than left on screen disabled. That is rule 2 again from
   * the other side: once the stage leaves `awaiting-VP` the POST refuses BOTH rulings, and `Gate`
   * deliberately does not gate its reject button on `armed` — so a gate left mounted here would offer
   * a live-looking "Reject" the server would 422. A control the API would refuse must not exist.
   */
  const determined =
    gate?.status === 'approved' || (components.length > 0 && confirmed === components.length);

  /** The codes dosing actually finalized for a component — the only signable set (POST 422s any other). */
  const finalized = (componentId: string) =>
    (dosing?.codes ?? []).filter((k) => k.componentId === componentId).map((k) => k.ratioSignature);

  /**
   * The components whose chosen code the POST would reject: none chosen (`''`), or one that is not in
   * the DosingDoc. This mirrors the endpoint's own membership check rather than guessing at armability
   * — and it can only ever WITHHOLD the pen, never grant it, because arming still requires
   * `gate.armable` from the server.
   */
  const unsignable = confirmations.filter((c) => !finalized(c.componentId).includes(c.code));

  /**
   * The gate's requirements are the SERVER's blockers, one per line, plus the one condition this
   * screen owns: a finalized code chosen for every component. Nothing else is invented here — in
   * particular not MSDS, which gates orders rather than the gate.
   */
  const requirements: Requirement[] = [
    {
      id: 'server',
      label: 'Every gate condition met',
      met: Boolean(gate?.armable),
      detail:
        gate && gate.blockers.length > 0 ? (
          <ul style={{ margin: '4px 0 0', paddingLeft: 16 }}>
            {gate.blockers.map((b) => (
              <li key={b}>{b}</li>
            ))}
          </ul>
        ) : undefined,
    },
    {
      id: 'codes',
      label: 'A finalized code chosen for every component',
      met: components.length > 0 && unsignable.length === 0,
      detail:
        components.length === 0
          ? 'There is no decision to sign.'
          : unsignable.length > 0
            ? unsignable
                .map((c) =>
                  c.code === ''
                    ? `${c.componentId}: no code proposed`
                    : `${c.componentId}: '${c.code}' is not one of dosing's finalized codes`,
                )
                .join(' · ')
            : undefined,
    },
  ];

  return (
    <section className="screen">
      <div className="cap">
        <b>Final determination — the last hard gate</b>
        Approval releases procurement and writes the Marker Library and Learned Conclusions.
      </div>

      <StageStatusCard name="Decision" state={stage} />

      {phase === 'absent' && (
        <EmptyState
          icon="ti-gavel"
          title="No decision assembled yet."
          body={
            <>
              The Decision stage assembles the matrix from the compliant set once the regulatory gate is
              signed and dosing has produced codes. There is nothing to sign until it has.
            </>
          }
        />
      )}

      {phase === 'ready' && (
        <>
          <div className="stat-strip">
            <StatCard
              label="Components"
              value={`${confirmed}/${components.length}`}
              hint="carrying a signed code"
            />
            <StatCard
              label="Blocking rows"
              value={blocking.length}
              tone={blocking.length > 0 ? 'danger' : undefined}
              hint={blocking.length > 0 ? 'a criterion is not cleared' : 'none'}
            />
            <StatCard
              label="Procurement"
              value={doc?.procurement.status ?? 'unreleased'}
              tone={released ? undefined : 'warning'}
              hint={`${doc?.procurement.orderedCas.length ?? 0} ordered`}
            />
            {gate?.status === 'approved' ? (
              <StatCard label="VP determination" value="approved" hint={gate.approvedAt ?? 'signed'} />
            ) : determined ? (
              <StatCard label="VP determination" value="signed" hint="a signature on every component" />
            ) : (
              /* `absent` renders an em-dash. An unsigned gate has no determination to report, and a
                 tile that guessed "pending" would invent a state the record does not hold. */
              <StatCard label="VP determination" absent hint="not signed" />
            )}
          </div>

          {components.map((c) => (
            <ComponentBand
              key={c.componentId}
              component={c}
              projectId={project.projectId}
              codes={(dosing?.codes ?? [])
                .filter((k) => k.componentId === c.componentId)
                .map((k) => k.ratioSignature)}
              chosen={choice[c.componentId] ?? c.proposedCode?.ratioSignature ?? ''}
              onChoose={(code) => setChoice((prev) => ({ ...prev, [c.componentId]: code }))}
              expanded={expanded}
              setExpanded={setExpanded}
            />
          ))}

          {released && (
            <Procurement
              components={components}
              dosing={dosing}
              sheets={sheets}
              ordered={doc?.procurement.orderedCas ?? []}
              ordering={ordering}
              error={orderError}
              onOrder={order}
            />
          )}

          <SectionHeader eyebrow="Determination" hint="the last signature in the journey" />

          {signError && (
            <div className="banner warn" role="alert" style={{ marginBottom: 8 }}>
              <i className="ti ti-alert-triangle" aria-hidden="true" />
              <div>
                <b>The determination was refused.</b>
                <div className="tiny" style={{ marginTop: 3 }}>{signError}</div>
              </div>
            </div>
          )}

          {determined ? (
            <div className="banner info" role="status">
              <i className="ti ti-signature" aria-hidden="true" />
              <div>
                <b>The determination is on the record.</b>
                <div className="tiny" style={{ marginTop: 3 }}>
                  Each component above carries the code that was signed, and who signed it. There is no
                  second pen here: a determination is made once, and the endpoint refuses another as soon
                  as the stage leaves its park.
                  {!released && (
                    <>
                      {' '}
                      Procurement is still unreleased: the orchestrator releases it by reacting to the
                      signed gate, not the signing call. Reload in a moment for the order controls — this
                      screen will not invent them ahead of the record.
                    </>
                  )}
                </div>
              </div>
            </div>
          ) : (
            <Gate
              kind="hard"
              title="VP R&D gate"
              records="releases procurement · writes the Marker Library + Learned Conclusions"
              requirements={requirements}
              signLabel="Approve & close project"
              rejectLabel="Reject (requires a reason)"
              signNote={{ placeholder: 'What was reviewed, and why this determination' }}
              onSign={sign}
              onReject={reject}
              signBusy={busy === 'sign'}
              rejectBusy={busy === 'reject'}
            />
          )}
        </>
      )}
    </section>
  );
}

/** One component's decision: the code (proposed or signed), then the rows that justify it. */
function ComponentBand({
  component: c,
  projectId,
  codes,
  chosen,
  onChoose,
  expanded,
  setExpanded,
}: {
  component: ComponentDecision;
  projectId: string;
  codes: string[];
  chosen: string;
  onChoose: (code: string) => void;
  expanded: string | null;
  setExpanded: (v: string | null) => void;
}) {
  const signed = c.confirmedCode !== null;

  return (
    <div style={{ marginBottom: 18 }}>
      <SectionHeader eyebrow={c.componentId} count={c.rows.length} hint="substances in this decision" />

      {/*
        Law 9 as pixels. Signed: a solid chip, the signer, the reason. Unsigned: the word "Proposed",
        a muted chip, and a picker — because until a human chooses, this is an offer.
      */}
      {signed ? (
        <div className="card" style={{ marginBottom: 10 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <span className="tiny muted">Confirmed code</span>
            <span className="chip chip--neutral chip--mono">{c.confirmedCode}</span>
            {c.confirmedBy && <span className="tiny muted">signed by {c.confirmedBy}</span>}
          </div>
          {c.confirmedReason && (
            <p className="small secondary" style={{ margin: '6px 0 0' }}>
              {c.confirmedReason}
            </p>
          )}
        </div>
      ) : (
        <div className="card" style={{ marginBottom: 10 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <span className="tiny muted">Proposed by the agent</span>
            {/*
              The signature appears ONCE, and where the decision is actually made: inside the picker,
              whose default is the proposal. Printing it as a chip beside the picker as well would put
              the same code on screen twice in two treatments, and the read-only one is exactly the
              shape a signed code takes — which is how a proposal starts looking like a signature.
              The chip is therefore the FALLBACK, for the case where dosing offers nothing to pick.
            */}
            {!c.proposedCode ? (
              <span className="tiny" style={{ color: 'var(--text-danger)' }}>
                no proposed code — this component cannot be signed
              </span>
            ) : (
              codes.length === 0 && (
                <span className="chip chip--mono" style={{ opacity: 0.75 }}>
                  {c.proposedCode.ratioSignature}
                </span>
              )
            )}
          </div>
          {c.proposedCode && (
            <p className="small secondary" style={{ margin: '6px 0 8px' }}>
              {c.proposedCode.rationale}
            </p>
          )}
          {codes.length > 0 && (
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
              <span className="tiny muted">Code to confirm for {c.componentId}</span>
              <select
                aria-label={`Code to confirm for ${c.componentId}`}
                value={chosen}
                onChange={(e) => onChoose(e.target.value)}
                style={{ font: 'inherit', fontSize: 'var(--t-small)', padding: '4px 6px' }}
              >
                {codes.map((code) => (
                  <option key={code} value={code}>
                    {code}
                    {code === c.proposedCode?.ratioSignature ? " — the agent's proposal" : ''}
                  </option>
                ))}
              </select>
            </label>
          )}
        </div>
      )}

      <table className="mx">
        <thead>
          <tr>
            <th>Substance</th>
            <th>Determination</th>
            <th>ppm</th>
            {CRITERIA.map((k) => (
              <th key={k} style={{ textAlign: 'center', textTransform: 'capitalize' }}>
                {k}
              </th>
            ))}
            <th style={{ width: 60 }}>Trace</th>
          </tr>
        </thead>
        <tbody>
          {c.rows.map((r) => {
            const key = `${c.componentId}|${r.cas}`;
            const isOpen = expanded === key;
            return (
              <Fragment key={key}>
                <tr style={isOpen ? { background: 'var(--surface-2)' } : undefined}>
                  <td>
                    <span style={{ fontWeight: 500 }}>{r.element}</span>{' '}
                    <span className="tiny muted">
                      <Data kind="code">{r.cas}</Data>
                    </span>
                  </td>
                  <td className="tiny">{r.determination}</td>
                  <td className="secondary" style={{ fontVariantNumeric: 'tabular-nums' }}>
                    {r.recommendedPpm}
                  </td>
                  {CRITERIA.map((k) => (
                    <td key={k} style={{ textAlign: 'center' }}>
                      <span
                        className={`chip ${r.cleared[k] ? 'v' : 'x'}`}
                        title={`${k} — ${r.cleared[k] ? 'clear' : 'blocking'} (owned by ${OWNER[k].label})`}
                      >
                        {r.cleared[k] ? '✓' : '✕'}
                      </span>
                    </td>
                  ))}
                  <td>
                    <button className="btn" onClick={() => setExpanded(isOpen ? null : key)} aria-expanded={isOpen}>
                      {isOpen ? 'Hide' : 'View'}
                    </button>
                  </td>
                </tr>
                {isOpen && (
                  <tr>
                    {/* 7 columns: substance, determination, ppm, three criteria, trace. */}
                    <td colSpan={7} style={{ padding: 0, background: 'var(--surface-2)' }}>
                      <div style={{ borderLeft: '2px solid var(--text-accent)', padding: 'var(--s3)' }}>
                        <div className="tiny muted" style={{ marginBottom: 6 }}>
                          Each criterion is owned by the stage that produced it. The record id is what
                          the claim was read from — the record is the truth, not this copy of it.
                        </div>
                        {CRITERIA.map((k) => (
                          <div className="step" key={k}>
                            <i
                              className={`ti ${r.cleared[k] ? 'ti-check' : 'ti-x'}`}
                              aria-hidden="true"
                              style={{
                                color: r.cleared[k] ? 'var(--text-success)' : 'var(--text-danger)',
                                marginTop: 2,
                              }}
                            />
                            <div>
                              <span style={{ textTransform: 'capitalize' }}>{k}</span> —{' '}
                              {r.cleared[k] ? 'clear' : <b>blocking</b>}{' '}
                              <Link to={`/p/${projectId}/${OWNER[k].stage}`}>
                                {OWNER[k].label} <i className="ti ti-arrow-right" aria-hidden="true" />
                              </Link>{' '}
                              <Data kind="code">
                                {k === 'regulatory'
                                  ? r.traceability.verdict
                                  : k === 'dosing'
                                    ? r.traceability.window
                                    : r.traceability.audit}
                              </Data>
                            </div>
                          </div>
                        ))}
                      </div>
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/**
 * Procurement — visible only once the record says `released`.
 *
 * The orderable set is the markers of CONFIRMED codes, never the decision rows and never a proposal:
 * "you cannot order what the VP did not sign". Each order is independently gated on a REVIEWED MSDS
 * (§5), and the button is disabled with the reason rather than hidden — a missing safety sheet is
 * what blocks an order, and hiding the control would hide the blocker with it.
 */
function Procurement({
  components,
  dosing,
  sheets,
  ordered,
  ordering,
  error,
  onOrder,
}: {
  components: ComponentDecision[];
  dosing: DosingDoc | null;
  sheets: MsdsEntry[];
  ordered: string[];
  ordering: string | null;
  error: string | null;
  onOrder: (cas: string) => void;
}) {
  const markers = components
    .filter((c) => c.confirmedCode !== null)
    .flatMap((c) =>
      (dosing?.codes ?? [])
        .filter((k) => k.componentId === c.componentId && k.ratioSignature === c.confirmedCode)
        .flatMap((k) => k.markers.map((m) => ({ ...m, componentId: c.componentId }))),
    );

  return (
    <>
      <SectionHeader
        eyebrow="Procurement"
        count={markers.length}
        hint="the markers of the signed codes — each order gated on a reviewed MSDS"
      />

      {error && (
        <div className="banner warn" role="alert" style={{ marginBottom: 8 }}>
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The order was refused.</b>
            <div className="tiny" style={{ marginTop: 3 }}>{error}</div>
          </div>
        </div>
      )}

      <table className="mx">
        <thead>
          <tr>
            <th>Substance</th>
            <th>Component</th>
            <th>MSDS</th>
            <th style={{ width: 90 }} />
          </tr>
        </thead>
        <tbody>
          {markers.map((m) => {
            const sheet = sheets.find((s) => s.cas === m.cas);
            const reviewed = sheet?.reviewStatus === 'reviewed';
            const isOrdered = ordered.includes(m.cas);
            return (
              <tr key={`${m.componentId}|${m.cas}`}>
                <td>
                  <span style={{ fontWeight: 500 }}>{m.element}</span>{' '}
                  <span className="tiny muted">
                    <Data kind="code">{m.cas}</Data>
                  </span>
                </td>
                <td className="tiny">{m.componentId}</td>
                <td className="tiny">
                  {reviewed ? (
                    <span style={{ color: 'var(--text-success)' }}>reviewed</span>
                  ) : (
                    <span style={{ color: 'var(--text-danger)' }}>
                      {sheet ? sheet.reviewStatus : 'no sheet on file'}
                    </span>
                  )}
                </td>
                <td>
                  {isOrdered ? (
                    <span className="chip chip--neutral">ordered</span>
                  ) : (
                    <button
                      className="btn"
                      disabled={!reviewed || ordering === m.cas}
                      onClick={() => onOrder(m.cas)}
                      title={
                        reviewed
                          ? undefined
                          : 'MSDS-before-order: a reviewed safety sheet is required before this can be ordered'
                      }
                    >
                      Order
                    </button>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </>
  );
}
