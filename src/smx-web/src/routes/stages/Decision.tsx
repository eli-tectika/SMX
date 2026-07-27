import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react';
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
import { Procurement } from '../../components/Procurement';
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
  /**
   * Whether the MSDS read itself failed — which is NOT "no sheet on file", and here the difference is
   * load-bearing twice over: this is the screen that PLACES the order. See `Procurement`.
   */
  const [sheetsUnknown, setSheetsUnknown] = useState(false);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [errMsg, setErrMsg] = useState<string>();
  /** A post-action re-read failed: the record on screen is the last good one and may be stale. */
  const [staleMsg, setStaleMsg] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [choice, setChoice] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<'sign' | 'reject' | null>(null);
  const [signError, setSignError] = useState<string | null>(null);
  const [orderError, setOrderError] = useState<string | null>(null);
  const [ordering, setOrdering] = useState<string | null>(null);
  /**
   * That a rejection was recorded IN THIS SESSION.
   *
   * It has to be held here because the wire cannot tell us: `GET /gate/vp` reports
   * `status = gate?.Status ?? "locked"`, and the rejection branch writes a gate whose status is
   * literally `"locked"` — so *rejected* and *never signed* are the same three bytes. The stage also
   * stays parked at `awaiting-VP` and no `confirmedCode` is written, so nothing else on the record
   * moves either, and without this flag a successful rejection looks exactly like a no-op.
   *
   * The DURABLE fix is a BACKEND change: `GET /gate/vp` would have to project the gate's `Reason` and
   * a status that distinguishes a rejection from an unsigned gate. This pass is frontend-only, so what
   * is deliverable here is the acknowledgment plus this note. Until then a reload loses the fact.
   */
  const [rejectedHere, setRejectedHere] = useState(false);
  /** The `generatedAt` of the doc currently in state — how a re-read notices the decision changed. */
  const generatedAtRef = useRef<string | null>(null);
  /**
   * A cancellation token for the re-reads that happen OUTSIDE the effect (after a sign, a rejection or
   * an order). The effect owns a local one; these calls had none, so an unmount mid-flight left them
   * writing into a dead component.
   */
  const alive = useRef({ cancelled: false });
  useEffect(() => {
    const token = alive.current;
    token.cancelled = false;
    return () => {
      token.cancelled = true;
    };
  }, []);

  const load = useCallback(
    async (signal?: { cancelled: boolean }, opts?: { keepRecord?: boolean }) => {
      // The MSDS read fails INDEPENDENTLY of the three project reads, the same separation Cost.tsx
      // makes and for the same reason: the registry is a CROSS-PROJECT surface that only the order
      // rows need, and a hiccup on it must not replace the decision, the evidence and the gate with
      // "the decision record could not be read" — a sentence that would also be false, since it was.
      const sheetsRead = getMsdsRegistry().then(
        (entries) => ({ ok: true, entries }),
        () => ({ ok: false, entries: [] as MsdsEntry[] }),
      );
      try {
        const [d, g, dose] = await Promise.all([
          getDecision(project.projectId),
          getVpGate(project.projectId),
          getDosing(project.projectId),
        ]);
        if (signal?.cancelled) return;
        setStaleMsg(null);
        setGate(g);
        setDosing(dose === NotFound ? null : dose);
        if (d === NotFound) {
          setDoc(null);
          generatedAtRef.current = null;
          setPhase('absent');
        } else {
          // The operator's per-component overrides are picks against a SPECIFIC decision. When the
          // decision itself has been regenerated underneath them (a revise, a re-pick), a surviving
          // pick is a choice nobody made about the rows now on screen — and it is still submittable.
          if (generatedAtRef.current !== null && generatedAtRef.current !== d.generatedAt) {
            setChoice({});
          }
          generatedAtRef.current = d.generatedAt;
          setDoc(d);
          setPhase('ready');
        }
      } catch (err) {
        if (signal?.cancelled) return;
        const msg = err instanceof Error ? err.message : String(err);
        if (opts?.keepRecord) {
          // A POST-ACTION re-read. Blanking the screen here would unmount the `ready` subtree and take
          // the banner explaining WHY the determination was refused with it — the single most important
          // message on this screen, destroyed by a transient failure to re-read. The record is already
          // on screen; keep it, and say plainly that it may now be stale.
          setStaleMsg(msg);
        } else {
          setErrMsg(msg);
          setPhase('error');
        }
        return;
      }
      const ms = await sheetsRead;
      if (signal?.cancelled) return;
      setSheets(ms.entries);
      setSheetsUnknown(!ms.ok);
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

  /*
   * All three actions re-read with `keepRecord`, and the re-read is EXPLICIT rather than left to
   * `refreshProject()`. It has to be: this effect keys on the stage `status`, and neither ruling
   * changes it synchronously — a rejection leaves the stage parked at `awaiting-VP` forever, and an
   * approval is closed by the ORCHESTRATOR reacting to the gate, seconds later. Dropping the explicit
   * call would leave a signed determination showing an unsigned, armed gate.
   */
  const sign = useCallback(
    async (note?: string) => {
      if (!note) {
        // Unreachable today — `Gate` keeps the pen disabled until the note is non-blank (`noteReady`).
        // Kept as a loud refusal rather than a silent `return`: a signature that quietly does nothing
        // is the worst outcome available on this screen.
        setSignError('A note is required — it records what was reviewed.');
        return;
      }
      setBusy('sign');
      setSignError(null);
      setRejectedHere(false);
      try {
        await recordVpDetermination(project.projectId, {
          determination: 'approved',
          reason: note,
          confirmations,
        });
        refreshProject();
        await load(alive.current, { keepRecord: true });
      } catch (err) {
        // The server re-checks and can refuse a button that looked enabled (a concurrent revise, a
        // stage that left its park). Show its words and re-read the gate for the fresh blockers.
        setSignError(err instanceof ApiError ? err.message : String(err));
        await load(alive.current, { keepRecord: true });
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
        setRejectedHere(true);
        refreshProject();
        await load(alive.current, { keepRecord: true });
      } catch (err) {
        setSignError(err instanceof ApiError ? err.message : String(err));
        await load(alive.current, { keepRecord: true });
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
        await load(alive.current, { keepRecord: true });
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
   * When it has, the pen is withdrawn entirely rather than left on screen disabled. That is rule 2
   * again from the other side: once the stage leaves `awaiting-VP` the POST refuses BOTH rulings, so
   * every control in the gate is dead — and a signing surface that keeps drawing a dead "Approve &
   * close project" beside a signed determination is inviting a second pen at exactly the moment there
   * is none. A gate the API would refuse in full must not be on screen at all.
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
      /*
       * APPROVE-ONLY, and verified against the endpoint rather than assumed: DecisionEndpoints.cs
       * returns from its `rejected` branch (writing the locked gate + reason) BEFORE it reads dosing
       * or walks the confirmations. So a component with no signable code blocks an approval and not a
       * rejection — and rejection is the escape hatch on exactly the state this requirement describes.
       */
      appliesTo: 'sign',
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

      {staleMsg && (
        <div className="banner warn" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The record could not be re-read after that action.</b>
            <div className="tiny" style={{ marginTop: 3 }}>
              What is shown below is the last good read and may now be out of date — reload before
              acting on it. {staleMsg}
            </div>
          </div>
        </div>
      )}

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
              sheetsUnknown={sheetsUnknown}
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

          {/*
            The acknowledgment a rejection otherwise never gets. The server records it and then
            nothing observable moves: the gate stays "locked", the stage stays parked, no
            `confirmedCode` appears — so the gate below re-renders LIVE AND ARMED, which is correct
            (the endpoint really does allow a re-determination) but reads as "nothing happened" while
            still offering "Approve & close project". Saying so is the whole fix available from here;
            the durable one is the backend change described on `rejectedHere` above.
          */}
          {rejectedHere && !determined && (
            <div className="banner info" role="status" style={{ marginBottom: 8 }}>
              <i className="ti ti-ban" aria-hidden="true" />
              <div>
                <b>The rejection was recorded with your reason; the gate is locked.</b>
                <div className="tiny" style={{ marginTop: 3 }}>
                  The stage stays parked at <span className="data">awaiting-VP</span>, so the gate below
                  is still live — the endpoint permits a re-determination after a rejection. Approving
                  now would supersede it.
                </div>
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
          {/* The wrapping <label> IS the accessible name. An identical `aria-label` on the select was a
              second, competing source for the same string — one label, not two. */}
          {codes.length > 0 && (
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
              <span className="tiny muted">Code to confirm for {c.componentId}</span>
              <select
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
        <caption className="sr-only">
          The substances in {c.componentId}&rsquo;s decision: each row&rsquo;s determination, its
          recommended ppm, and whether it clears regulatory, dosing and cost. &ldquo;View&rdquo; opens
          the trace to the stage that owns each criterion.
        </caption>
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
                        {/* The glyph and the `title` carry the meaning visually; neither is announced
                            reliably. Whether a criterion BLOCKS is the cell's entire content. */}
                        <span className="sr-only">
                          {' '}
                          {k} — {r.cleared[k] ? 'clear' : 'blocking'}
                        </span>
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
                    <td
                      colSpan={4 + CRITERIA.length}
                      style={{ padding: 0, background: 'var(--surface-2)' }}
                    >
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
