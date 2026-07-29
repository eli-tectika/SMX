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
  DecisionRow,
  DosingDoc,
  MsdsEntry,
  VpGate as VpGateState,
} from '../../api/types';
import { Loading } from '../../components/Loading';
import { Procurement, type SheetsState } from '../../components/Procurement';
import { Data } from '../../components/ui/Data';
import { EmptyState, SectionHeader, StatCard } from '../../components/ui/Primitives';
import type { ScreenProps } from '../ProjectLayout';

const CRITERIA = ['regulatory', 'dosing', 'cost'] as const;
type Criterion = (typeof CRITERIA)[number];

/** Each criterion is owned by the stage that produced it, so "trace" is a link plus the record id. */
const OWNER: Record<Criterion, { stage: string; label: string }> = {
  regulatory: { stage: 'regulatory', label: 'Regulatory gate' },
  dosing: { stage: 'dosing', label: 'Dosing & codes' },
  cost: { stage: 'cost', label: 'Cost & availability' },
};

/**
 * WHO signed the VP gate, folded to two cases — and `null` and `undefined` collapse to the same one.
 *
 * `'operator'` is the human recording VP R&D's determination, the only writer this endpoint has.
 * `null` is "the record does not say" (a gate written before the field existed) and an absent key is
 * an older API build or a skewed deploy. Neither is evidence a person signed, so both land on
 * `unknown`. Written as an allow-list rather than `?? 'unknown'` on purpose: a signer string this
 * build has never heard of — a future `'vp'`, a typo in a seed script — must also fall to unknown
 * rather than through a default that happens to read as a person.
 *
 * Only meaningful on an APPROVED gate: a locked gate can carry a stale signer for one write, and a
 * screen that rendered the signer whenever it was non-null would print a signature over a gate
 * nothing has approved.
 */
type Signer = 'operator' | 'unknown';

function signerOf(gate: VpGateState): Signer {
  return gate.approvedBy === 'operator' ? 'operator' : 'unknown';
}

/** A date, or nothing. Never a slice of whatever the wire happened to carry. */
function signedOn(at: string | null | undefined): string | null {
  return typeof at === 'string' && at.length >= 10 ? at.slice(0, 10) : null;
}

/*
 * Every read below is defensive. `client.ts` casts responses with `as` and validates nothing, so a
 * backend that drifts on any of these arrays turns a `.map` into a TypeError — and a throw here
 * unmounts the screen that signs the last hard gate. Degrade inside the region instead.
 */
const componentsOf = (doc: DecisionDoc | null): ComponentDecision[] =>
  doc && Array.isArray(doc.components) ? doc.components.filter((c) => Boolean(c)) : [];
const rowsOf = (c: ComponentDecision): DecisionRow[] => (Array.isArray(c.rows) ? c.rows : []);
const isClear = (r: DecisionRow, k: Criterion): boolean => r.cleared?.[k] === true;

/**
 * Decision — the signed close, read as one sequence: what was decided, the determination, and what
 * the determination releases.
 *
 * The three sections are steps in time, not three views of the same thing, and the order is the
 * anti-rubber-stamping law as layout: the evidence comes BEFORE the pen, and procurement exists only
 * after the pen. Four things this screen exists to get right:
 *
 *  1. **A proposal is not a signature.** `proposedCode` is the agent's offer, `confirmedCode` the
 *     VP's; they never share a treatment. And the proposal is HISTORY — once signed it stays on
 *     screen beside the signature, because the audit trail is what the agent said next to what the
 *     VP signed, not the second overwriting the first.
 *  2. **Armability is the server's word.** `GET /gate/vp` runs the same checks the POST enforces,
 *     including the two the browser cannot see (a stage no longer parked at `awaiting-VP`, a
 *     revision in flight). A tally assembled here could advertise a pen the POST refuses, and the
 *     POST re-checks — so when it refuses, we re-read and show the fresh blockers.
 *  3. **Who signed is a fact, not a default.** An approved gate with no recorded signer reads as
 *     unknown provenance and never as a person.
 *  4. **MSDS-before-order is a hard precondition, stated where the order is placed.** It is not a
 *     condition of the gate — it gates each individual order, and release itself is eventually
 *     consistent (the pipeline flips procurement by reacting to the approved gate, not by the
 *     signing call), so signing re-reads rather than assuming.
 */
export function Decision({ project, refreshProject }: ScreenProps) {
  const stage = project.stages.decision;
  const status = stage?.status;

  const [doc, setDoc] = useState<DecisionDoc | null>(null);
  const [gate, setGate] = useState<VpGateState | null>(null);
  const [dosing, setDosing] = useState<DosingDoc | null>(null);
  const [sheets, setSheets] = useState<MsdsEntry[]>([]);
  /**
   * How far the MSDS read has got — NOT a boolean, and not derivable from `sheets` being empty.
   *
   * `phase` reaches `ready` on the three project reads while this one is still in flight (see
   * `load`), so `[]` covers three different situations: not read yet, read and empty, could not be
   * read. Only the middle one may be described as "no sheet on file", and this is the screen that
   * PLACES the order. See `SheetsState`.
   */
  const [sheetsState, setSheetsState] = useState<SheetsState>('unread');
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [errMsg, setErrMsg] = useState<string>();
  /** A post-action re-read failed: the record on screen is the last good one and may be stale. */
  const [staleMsg, setStaleMsg] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [choice, setChoice] = useState<Record<string, string>>({});
  const [note, setNote] = useState('');
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
   * The durable fix is a BACKEND change: `GET /gate/vp` would have to project the gate's `Reason`
   * and a status that distinguishes a rejection from an unsigned gate. Until then a reload loses it.
   */
  const [rejectedHere, setRejectedHere] = useState(false);
  /** The `generatedAt` of the doc currently in state — how a re-read notices the decision changed. */
  const generatedAtRef = useRef<string | null>(null);
  /**
   * A cancellation token for the re-reads that happen OUTSIDE the effect (after a sign, a rejection
   * or an order). The effect owns a local one; these calls had none, so an unmount mid-flight left
   * them writing into a dead component.
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
      // The MSDS read fails INDEPENDENTLY of the three project reads: the registry is a CROSS-PROJECT
      // surface that only the order rows need, and a hiccup on it must not replace the decision, the
      // evidence and the gate with "the decision record could not be read" — a sentence that would
      // also be false, since it was read.
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
          // A POST-ACTION re-read. Blanking the screen here would unmount the `ready` subtree and
          // take the banner explaining WHY the determination was refused with it — the single most
          // important message on this screen, destroyed by a transient failure to re-read. The
          // record is already on screen; keep it, and say plainly that it may now be stale.
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
      // Deliberately never reset to 'unread' on a RE-read: sheets already read stay believable while
      // the next read is in flight. Only the first load has genuinely nothing to say.
      setSheetsState(ms.ok ? 'ok' : 'failed');
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

  const components = componentsOf(doc);
  /** The doc came back, but not shaped like a decision. Say so; do not iterate it. */
  const malformed = doc !== null && !Array.isArray(doc.components);

  /** The code each component will be signed with: the agent's proposal until the VP picks another. */
  const confirmations = useMemo(
    () =>
      componentsOf(doc).map((c) => ({
        componentId: c.componentId,
        code: choice[c.componentId] ?? c.proposedCode?.ratioSignature ?? '',
      })),
    [doc, choice],
  );

  /*
   * All three actions re-read with `keepRecord`, and the re-read is EXPLICIT rather than left to
   * `refreshProject()`. It has to be: the load effect keys on the stage `status`, and neither ruling
   * changes it synchronously — a rejection leaves the stage parked at `awaiting-VP` forever, and an
   * approval is closed by the pipeline reacting to the gate, seconds later. Dropping the explicit
   * call would leave a signed determination showing an unsigned, armed gate.
   */
  const sign = useCallback(async () => {
    const reason = note.trim();
    if (!reason) {
      // Unreachable while the button honours `canSign`. Kept as a loud refusal rather than a silent
      // `return`: a signature that quietly does nothing is the worst outcome available here.
      setSignError('A note is required — it records what was reviewed.');
      return;
    }
    setBusy('sign');
    setSignError(null);
    setRejectedHere(false);
    try {
      await recordVpDetermination(project.projectId, {
        determination: 'approved',
        reason,
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
  }, [project.projectId, confirmations, note, load, refreshProject]);

  const reject = useCallback(async () => {
    const reason = note.trim();
    if (!reason) {
      setSignError('A reason is required — a rejection is a ruling, not a dismissal.');
      return;
    }
    setBusy('reject');
    setSignError(null);
    try {
      await recordVpDetermination(project.projectId, { determination: 'rejected', reason });
      setRejectedHere(true);
      // A recorded ruling consumes its reason: the gate stays live, and a SECOND ruling is a second
      // ruling, which needs its own words rather than inheriting the ones already on the record.
      setNote('');
      refreshProject();
      await load(alive.current, { keepRecord: true });
    } catch (err) {
      setSignError(err instanceof ApiError ? err.message : String(err));
      await load(alive.current, { keepRecord: true });
    } finally {
      setBusy(null);
    }
  }, [project.projectId, note, load, refreshProject]);

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
        <div className="banner danger" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div className="prose">
            <b>The decision record could not be read.</b> {errMsg}
          </div>
        </div>
      </section>
    );
  }

  const rows = components.flatMap(rowsOf);
  const blocking = rows.filter((r) => CRITERIA.some((k) => !isClear(r, k)));
  const confirmed = components.filter((c) => c.confirmedCode !== null).length;
  const released = doc?.procurement?.status === 'released';
  const orderedCas = Array.isArray(doc?.procurement?.orderedCas) ? doc!.procurement.orderedCas : [];
  const codes = Array.isArray(dosing?.codes) ? dosing!.codes : [];
  const blockers = Array.isArray(gate?.blockers) ? gate!.blockers : [];

  const approved = gate?.status === 'approved';
  /** Only read on an approved gate — see `Signer`. */
  const signer: Signer | null = approved && gate ? signerOf(gate) : null;
  const when = approved ? signedOn(gate?.approvedAt) : null;

  /**
   * The determination has been made — either the gate record says so, or every component carries a
   * signature (both are written by the same endpoint, and either alone is proof one ran).
   *
   * When it has, the pen is WITHDRAWN entirely rather than left on screen disabled. Once the stage
   * leaves `awaiting-VP` the POST refuses both rulings, so every control here is dead — and a
   * signing block that keeps drawing a dead "Approve & close project" beside a signed determination
   * is inviting a second pen at exactly the moment there is none.
   */
  const determined = approved || (components.length > 0 && confirmed === components.length);

  /** The codes dosing actually finalized for a component — the only signable set (POST 422s any other). */
  const finalized = (componentId: string) =>
    codes.filter((k) => k.componentId === componentId).map((k) => k.ratioSignature);

  /**
   * The components whose chosen code the POST would reject: none chosen (`''`), or one that is not
   * in the DosingDoc. This mirrors the endpoint's own membership check rather than guessing at
   * armability — and it can only ever WITHHOLD the pen, never grant it, because arming still
   * requires `gate.armable` from the server.
   */
  const unsignable = confirmations.filter((c) => !finalized(c.componentId).includes(c.code));

  const armable = gate?.armable === true;
  const codesReady = components.length > 0 && unsignable.length === 0;
  const noteReady = note.trim().length > 0;
  /*
   * Rejection arms on the server's blockers but NOT on the codes: `DecisionEndpoints.cs` returns
   * from its `rejected` branch — writing the locked gate and the reason — before it reads dosing or
   * walks the confirmations. So a component with no signable code blocks an approval and not a
   * rejection, and rejection is the escape hatch on exactly that state.
   */
  const canSign = armable && codesReady && noteReady && busy === null;
  const canReject = armable && noteReady && busy === null;

  return (
    <>
      <section className="screen">
        <SectionHeader
          title="What was decided"
          headingLevel={3}
          count={components.length}
          hint="one track per component — the evidence the determination is made on"
        />

        {malformed && (
          <div className="banner danger" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div className="prose">
              <b>The decision record came back in a shape this screen cannot read.</b> It carries no
              component list, so nothing below can be shown and nothing here can be signed. Do not
              treat the empty screen as an empty decision.
            </div>
          </div>
        )}

        {phase === 'absent' && (
          <EmptyState
            icon="ti-gavel"
            title="No decision assembled yet."
            body={
              <>
                The Decision stage assembles the matrix from the compliant set once the regulatory
                gate is signed and dosing has produced codes. There is nothing to sign until it has.
              </>
            }
          />
        )}

        {phase === 'ready' && !malformed && (
          <>
            {/*
              Two tiles, and both are things the operator can act on: a blocking row is why the
              determination is not obvious, and an unsigned component is what the approval is
              waiting for. Procurement status and an ordered count were on this strip and are not
              any more — they are consequences of the signature, and the procurement section below
              states them where they are actionable.
            */}
            <div className="stat-strip">
              <StatCard
                label="Blocking rows"
                value={blocking.length}
                tone={blocking.length > 0 ? 'danger' : undefined}
                hint={blocking.length > 0 ? 'a criterion is not cleared' : 'every criterion clear'}
              />
              <StatCard
                label="Awaiting a signature"
                value={components.length - confirmed}
                tone={components.length - confirmed > 0 ? 'warning' : undefined}
                hint={
                  components.length - confirmed > 0
                    ? `of ${components.length} component${components.length === 1 ? '' : 's'}`
                    : 'every component signed'
                }
              />
            </div>

            {components.map((c) => (
              <ComponentBand
                key={c.componentId}
                component={c}
                projectId={project.projectId}
                codes={finalized(c.componentId)}
                chosen={choice[c.componentId] ?? c.proposedCode?.ratioSignature ?? ''}
                onChoose={(code) => setChoice((prev) => ({ ...prev, [c.componentId]: code }))}
                expanded={expanded}
                setExpanded={setExpanded}
              />
            ))}
          </>
        )}
      </section>

      <section className="screen">
        <SectionHeader title="The determination" headingLevel={3} />

        {staleMsg && (
          <div className="banner warn" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div className="prose">
              <b>The record could not be re-read after that action.</b> What is shown is the last
              good read and may now be out of date — reload before acting on it. {staleMsg}
            </div>
          </div>
        )}

        {signError && (
          <div className="banner danger" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div className="prose">
              <b>The determination was refused.</b> {signError}
            </div>
          </div>
        )}

        {/*
          The acknowledgment a rejection otherwise never gets. The server records it and then nothing
          observable moves: the gate stays "locked", the stage stays parked, no `confirmedCode`
          appears — so the block below re-renders live and armed, which is correct (the endpoint
          really does allow a re-determination) but reads as "nothing happened" while still offering
          "Approve & close project". Saying so is the whole fix available from here.
        */}
        {rejectedHere && !determined && (
          <div className="banner info" role="status">
            <i className="ti ti-ban" aria-hidden="true" />
            <div className="prose">
              <b>The rejection was recorded with your reason; the gate is locked.</b> The stage stays
              parked at <Data kind="code">awaiting-VP</Data>, so the gate below is still live — the
              endpoint permits a re-determination after a rejection. Approving now would supersede it.
            </div>
          </div>
        )}

        {determined ? (
          <SignedPanel signer={signer} when={when} released={released} />
        ) : (
          <SignBlock
            armable={armable}
            gateUnread={gate === null}
            blockers={blockers}
            codesReady={codesReady}
            unsignable={unsignable}
            noComponents={components.length === 0}
            note={note}
            setNote={setNote}
            canSign={canSign}
            canReject={canReject}
            busy={busy}
            onSign={() => void sign()}
            onReject={() => void reject()}
          />
        )}
      </section>

      {released && (
        <section className="screen">
          <SectionHeader
            title="Procurement"
            headingLevel={3}
            hint="what the signature released — each order still behind its safety sheet"
          />
          <Procurement
            components={components}
            dosing={dosing}
            sheets={sheets}
            sheetsState={sheetsState}
            ordered={orderedCas}
            ordering={ordering}
            error={orderError}
            onOrder={order}
          />
        </section>
      )}
    </>
  );
}

/**
 * Who signed, and when, in one line — or, where the record cannot say, in as many as it takes.
 *
 * The branches are deliberately not one parameterised box with a `tone`. They differ in what they
 * claim about a person, and that is exactly the difference a shared shell would flatten.
 */
function SignedPanel({
  signer,
  when,
  released,
}: {
  signer: Signer | null;
  when: string | null;
  released: boolean;
}) {
  /* Procurement release is eventually consistent: the pipeline flips it by reacting to the approved
     gate, not by the signing call. So an unreleased determination is normal for a few seconds, and
     this screen says so rather than inventing order controls ahead of the record. */
  const tail = released ? null : (
    <>
      {' '}
      Procurement is not released yet — the pipeline releases it by reacting to the signed gate, not
      by the signing call. Reload in a moment for the order controls.
    </>
  );

  if (signer === 'operator') {
    return (
      <div
        className="banner"
        data-signer="operator"
        style={{
          background: 'var(--bg-teal)',
          borderColor: 'var(--border-teal)',
          color: 'var(--text-teal)',
        }}
      >
        <i className="ti ti-writing-sign" aria-hidden="true" />
        <div className="prose">
          <b>You recorded VP R&amp;D&rsquo;s determination{when ? ` on ${when}` : ''}.</b> It wrote
          the Marker Library entry and the close conclusion, and released procurement. There is no
          second pen: a determination is made once, and the endpoint refuses another as soon as the
          stage leaves its park.
          {tail}
        </div>
      </div>
    );
  }

  if (signer === 'unknown') {
    return (
      /*
       * Approved, signer unrecorded. The temptation is to read this as the operator, because in
       * practice it was — and that is precisely why it must not: a gate written before the record
       * named its signer cannot be retroactively claimed as a person's, and the one build that
       * signs this gate without a person is the build whose gates would go unattributed. Withhold.
       */
      <div className="banner warn" data-signer="unknown" role="status">
        <i className="ti ti-help-circle" aria-hidden="true" />
        <div className="prose">
          <b>Approved{when ? ` on ${when}` : ''} &mdash; the record does not say who signed it.</b>{' '}
          The Marker Library entry and the close conclusion were written on this approval and
          procurement was released on it, but no signer is on the record. Do not read it as a
          person&rsquo;s determination.
          {tail}
        </div>
      </div>
    );
  }

  /*
   * Every component carries a signed code while the gate record does not report an approval — the
   * window between the POST landing and the gate read catching up, and also what a partially
   * written record looks like. The determination is real (the codes below are signed and name their
   * signer) but the gate is not this screen's witness to it, so nothing here is attributed.
   */
  return (
    <div className="banner info" data-signer="components" role="status">
      <i className="ti ti-signature" aria-hidden="true" />
      <div className="prose">
        <b>Every component carries a signed code.</b> The gate record does not report an approval
        yet, so this screen will not say who signed or when — each component above names its own
        signer. The pen is withdrawn regardless: the endpoint refuses a second determination.
        {tail}
      </div>
    </div>
  );
}

/**
 * The signature, with its preconditions attached to the buttons rather than floating above them in
 * a panel of their own.
 *
 * `armable` is the SERVER's answer and it is what enables the press. The checks below are the
 * server's blockers plus the one condition this screen owns, and they EXPLAIN the answer — they do
 * not compute it. A tally assembled in the browser can disagree with the endpoint that is about to
 * refuse it, and the direction it disagrees in is a live-looking button over an unarmed gate.
 */
function SignBlock({
  armable,
  gateUnread,
  blockers,
  codesReady,
  unsignable,
  noComponents,
  note,
  setNote,
  canSign,
  canReject,
  busy,
  onSign,
  onReject,
}: {
  armable: boolean;
  gateUnread: boolean;
  blockers: string[];
  codesReady: boolean;
  unsignable: { componentId: string; code: string }[];
  noComponents: boolean;
  note: string;
  setNote: (v: string) => void;
  canSign: boolean;
  canReject: boolean;
  busy: 'sign' | 'reject' | null;
  onSign: () => void;
  onReject: () => void;
}) {
  return (
    <div>
      <p className="prose" style={{ margin: '0 0 var(--s3)' }}>
        The last hard gate. Approving records VP R&amp;D&rsquo;s determination, writes the Marker
        Library entry and the close conclusion, and releases procurement — where each order is still
        held behind a reviewed safety sheet. Rejecting records the refusal and its reason, and leaves
        the project open.
      </p>

      {/* Named, because it is the one list on this screen a reader needs to be able to find: it is
          what stands between the operator and the last hard gate. */}
      <ul
        aria-label="Determination preconditions"
        style={{ listStyle: 'none', margin: '0 0 var(--s3)', padding: 0 }}
      >
        <Check
          met={armable}
          label="Every gate condition met"
          detail={
            gateUnread ? (
              'The gate could not be read, so this screen cannot say what it would accept. Nothing can be signed until it can.'
            ) : blockers.length > 0 ? (
              /* The VP gate's blockers are plain-English sentences meant to be shown verbatim —
                 unlike the regulatory gate's parseable "unreviewed: {cas}|{comp}" strings. */
              <ul style={{ margin: 0, paddingLeft: 16 }}>
                {blockers.map((b) => (
                  <li key={b}>{b}</li>
                ))}
              </ul>
            ) : armable ? undefined : (
              'The server will not arm this gate and gave no reason.'
            )
          }
        />
        <Check
          met={codesReady}
          // "approval only" is in the LABEL, not just the detail: an operator reading an unmet row
          // needs to know there, in the row, that it does not stand between them and a rejection.
          label="A finalized code chosen for every component (approval only)"
          detail={
            noComponents
              ? 'There is no decision to sign.'
              : unsignable.length > 0
                ? unsignable
                    .map((c) =>
                      c.code === ''
                        ? `${c.componentId}: no code proposed`
                        : `${c.componentId}: '${c.code}' is not one of dosing's finalized codes`,
                    )
                    .join(' · ')
                : undefined
          }
        />
      </ul>

      {/* One note, for whichever ruling is made. A rejection is a ruling and needs its reason
          exactly as much as an approval does — the backend 422s a blank one either way. */}
      <textarea
        value={note}
        onChange={(e) => setNote(e.target.value)}
        placeholder="What was reviewed, and why this determination"
        rows={2}
        aria-label="Determination note"
        disabled={busy !== null}
        style={{
          width: '100%',
          font: 'inherit',
          fontSize: 'var(--t-small)',
          padding: '6px 8px',
          border: '0.5px solid var(--border-strong)',
          borderRadius: 'var(--r1)',
          resize: 'vertical',
        }}
      />

      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 'var(--s3)',
          flexWrap: 'wrap',
          marginTop: 'var(--s3)',
        }}
      >
        <button
          className="btn primary"
          type="button"
          disabled={!canSign}
          onClick={onSign}
          title={
            !armable
              ? 'Locked until the checks above pass — the server refuses a signature until then'
              : !codesReady
                ? 'Every component needs a finalized code before an approval can be recorded'
                : !note.trim()
                  ? 'A note is required — it records what was reviewed'
                  : undefined
          }
        >
          <i className={`ti ${busy === 'sign' ? 'ti-loader' : 'ti-signature'}`} aria-hidden="true" />{' '}
          {busy === 'sign' ? 'Recording…' : 'Approve & close project'}
        </button>
        <button
          className="btn"
          type="button"
          disabled={!canReject}
          onClick={onReject}
          title={
            !armable
              ? 'Locked until the checks above pass — the endpoint refuses a rejection on an unarmed gate too'
              : !note.trim()
                ? 'A reason is required — a rejection is a ruling, not a dismissal'
                : undefined
          }
        >
          <i className={`ti ${busy === 'reject' ? 'ti-loader' : 'ti-ban'}`} aria-hidden="true" />{' '}
          {busy === 'reject' ? 'Recording…' : 'Reject (requires a reason)'}
        </button>
        <span className="small" style={{ color: 'var(--text-warning)' }}>
          {!armable
            ? 'Locked until the checks above pass — no determination can be recorded until then.'
            : !codesReady
              ? 'Locked for approval until every component has a finalized code — a rejection can still be recorded.'
              : !note.trim()
                ? 'A note is required — it records what was reviewed.'
                : 'Signing releases procurement and writes the Marker Library.'}
        </span>
      </div>
    </div>
  );
}

/** One precondition. Met is a statement of fact, not a celebration — no green tick. */
function Check({
  met,
  label,
  detail,
}: {
  met: boolean;
  label: string;
  detail?: React.ReactNode;
}) {
  return (
    <li
      style={{
        display: 'flex',
        alignItems: 'flex-start',
        gap: 'var(--s2)',
        padding: 'var(--s2) 0',
        borderTop: '1px solid var(--border)',
      }}
    >
      <i
        className={`ti ${met ? 'ti-check' : 'ti-x'}`}
        aria-hidden="true"
        style={{ color: met ? 'var(--text-muted)' : 'var(--text-warning)', marginTop: 2 }}
      />
      <span style={{ flex: 1, minWidth: 0 }}>
        <span className="small" style={{ color: met ? 'var(--text-muted)' : 'var(--text-primary)' }}>
          {label}
        </span>
        {detail && (
          <span className="small" style={{ display: 'block', color: 'var(--text-warning)' }}>
            {detail}
          </span>
        )}
      </span>
    </li>
  );
}

/** One component's decision: the code (proposed, signed, or both), then the rows that justify it. */
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
  const rows = rowsOf(c);
  const proposal = c.proposedCode?.ratioSignature;
  /** The VP signed something other than what the agent offered. The audit trail's whole point. */
  const overridden = signed && proposal !== undefined && proposal !== c.confirmedCode;

  return (
    <div style={{ marginBottom: 'var(--s5)' }}>
      <SectionHeader
        eyebrow={c.componentId}
        count={rows.length}
        hint="substances in this decision"
      />

      {signed ? (
        <div className="card" style={{ marginBottom: 10 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <span className="small muted">Confirmed code</span>
            <span className="chip chip--neutral chip--mono">{c.confirmedCode}</span>
            {c.confirmedBy && <span className="small muted">signed by {c.confirmedBy}</span>}
          </div>
          {c.confirmedReason && (
            <p className="prose" style={{ margin: '6px 0 0' }}>
              {c.confirmedReason}
            </p>
          )}
          {/*
            The proposal is HISTORY and is never overwritten: the audit trail keeps what the agent
            offered beside what the VP signed. Dropping it once signed would leave an override
            indistinguishable from a confirmation, which is the one comparison this record exists
            to make. Muted and labelled — it is a past offer, not a live one.
          */}
          {proposal !== undefined && (
            <p className="small muted" style={{ margin: '8px 0 0' }}>
              {overridden ? 'Overrode the agent’s proposal ' : 'The agent proposed '}
              <span className="chip chip--mono">{proposal}</span>
              {c.proposedCode?.rationale ? ` — ${c.proposedCode.rationale}` : ''}
            </p>
          )}
        </div>
      ) : (
        <div className="card" style={{ marginBottom: 10 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <span className="small muted">Proposed by the agent</span>
            {/*
              The signature appears ONCE, and where the decision is actually made: inside the picker,
              whose default is the proposal. Printing it as a chip beside the picker as well would
              put the same code on screen twice in two treatments, and the read-only one is exactly
              the shape a signed code takes — which is how a proposal starts looking like a
              signature. The chip is therefore the FALLBACK, for when dosing offers nothing to pick.
            */}
            {!c.proposedCode ? (
              <span className="small" style={{ color: 'var(--text-danger)' }}>
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
            <p className="prose" style={{ margin: '6px 0 8px' }}>
              {c.proposedCode.rationale}
            </p>
          )}
          {/* The wrapping <label> IS the accessible name. An identical `aria-label` on the select was
              a second, competing source for the same string — one label, not two. */}
          {codes.length > 0 && (
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
              <span className="small muted">Code to confirm for {c.componentId}</span>
              <select
                value={chosen}
                onChange={(e) => onChoose(e.target.value)}
                style={{ font: 'inherit', fontSize: 'var(--t-small)', padding: '4px 6px' }}
              >
                {codes.map((code) => (
                  <option key={code} value={code}>
                    {code}
                    {code === proposal ? ' — the agent’s proposal' : ''}
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
          recommended ppm, and whether it clears regulatory, dosing and cost. &ldquo;View&rdquo;
          opens the trace to the stage that owns each criterion.
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
          {rows.map((r) => {
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
                        className={`chip ${isClear(r, k) ? 'v' : 'x'}`}
                        title={`${k} — ${isClear(r, k) ? 'clear' : 'blocking'} (owned by ${OWNER[k].label})`}
                      >
                        {isClear(r, k) ? '✓' : '✕'}
                        {/* The glyph and the `title` carry the meaning visually; neither is
                            announced reliably. Whether a criterion BLOCKS is the cell's content. */}
                        <span className="sr-only">
                          {' '}
                          {k} — {isClear(r, k) ? 'clear' : 'blocking'}
                        </span>
                      </span>
                    </td>
                  ))}
                  <td>
                    <button
                      className="btn"
                      type="button"
                      onClick={() => setExpanded(isOpen ? null : key)}
                      aria-expanded={isOpen}
                    >
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
                      <div
                        style={{ borderLeft: '2px solid var(--text-accent)', padding: 'var(--s3)' }}
                      >
                        <div className="small muted" style={{ marginBottom: 6 }}>
                          Each criterion is owned by the stage that produced it. The record id is
                          what the claim was read from — the record is the truth, not this copy of it.
                        </div>
                        {CRITERIA.map((k) => (
                          <div className="step" key={k}>
                            <i
                              className={`ti ${isClear(r, k) ? 'ti-check' : 'ti-x'}`}
                              aria-hidden="true"
                              style={{
                                color: isClear(r, k) ? 'var(--text-success)' : 'var(--text-danger)',
                                marginTop: 2,
                              }}
                            />
                            <div>
                              <span style={{ textTransform: 'capitalize' }}>{k}</span> —{' '}
                              {isClear(r, k) ? 'clear' : <b>blocking</b>}{' '}
                              <Link to={`/p/${projectId}/${OWNER[k].stage}`}>
                                {OWNER[k].label} <i className="ti ti-arrow-right" aria-hidden="true" />
                              </Link>{' '}
                              <Data kind="code">
                                {k === 'regulatory'
                                  ? r.traceability?.verdict
                                  : k === 'dosing'
                                    ? r.traceability?.window
                                    : r.traceability?.audit}
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
