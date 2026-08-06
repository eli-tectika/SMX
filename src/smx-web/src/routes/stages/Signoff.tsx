import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  ApiError,
  NotFound,
  getDecision,
  getDosing,
  getMsdsRegistry,
  getVpGate,
  matrixXlsxUrl,
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
import { foldConfidence } from '../../domain/confidence';
import { LOW_CONFIDENCE } from '../../domain/matrixSummary';
import { citationsOf, confidencesOf, useProjectTable, type ReadRow } from './projectTable';
import type { ScreenProps } from '../ProjectLayout';

/**
 * The three criteria a decision row must clear.
 *
 * `availability` replaced `cost` when the Cost stage was deleted (spec §6): there are no prices to be
 * had, but supply is a real procurement blocker and dropping it silently would lose the only supply
 * signal in the product. The frontend `ClearedCriteria` type still names `cost` — that file belongs to
 * another change in flight — so the criterion is read through a string index rather than through the
 * type. That reads `false` for a key the record does not carry, which renders as BLOCKING: the loud
 * direction, and the only safe one for a checklist that gates procurement.
 */
const CRITERIA = ['regulatory', 'dosing', 'availability'] as const;
type Criterion = (typeof CRITERIA)[number];

/** Each criterion is owned by the phase that produced it, so "trace" is a link plus the record id. */
const OWNER: Record<Criterion, { slug: string; label: string; trace: 'verdict' | 'window' }> = {
  regulatory: { slug: 'regulatory', label: 'Regulatory', trace: 'verdict' },
  dosing: { slug: 'dosing', label: 'Dosing', trace: 'window' },
  // Availability rides on the dosing document — the supply audit moved there with Cost's deletion, so
  // a third trace ref would repeat `window` on every row.
  availability: { slug: 'dosing', label: 'Dosing — supply', trace: 'window' },
};

/**
 * The compliance-package export — what the REGULATORY signature releases.
 *
 * Built here rather than in `client.ts` because the client has no export helper yet and this screen may
 * not add one. It is the same shape `matrixXlsxUrl` uses (a plain href, same-origin through the Vite
 * proxy in dev and App Gateway's `/api/*` rule in Azure), and it should move into the client the moment
 * that file grows an export surface — two places knowing the API prefix is one too many.
 */
const compliancePackageUrl = (projectId: string) =>
  `/api/projects/${encodeURIComponent(projectId)}/regulatory/compliance-package`;

/**
 * WHO signed, folded through an allow-list — and `null` and `undefined` collapse to the same case.
 *
 * `'operator'` is the human recording the determination. `null` is "the record does not say" (a gate
 * written before the field existed) and an absent key is an older API build or a skewed deploy.
 * Neither is evidence a person signed, so both land on `unknown`. Never `?? 'unknown'`: a signer string
 * this build has never heard of — a future `'vp'`, a typo in a seed script — must also fall to unknown
 * rather than through a default that happens to read as a person.
 *
 * `auto-approve` was the REGULATORY gate's value and that gate is gone (spec §16.4). It stays in this
 * union anyway: the fold is an ALLOW-LIST, and its whole point is that a value this build does not
 * recognise falls to `unknown` rather than through a default that happens to read as a person. Should
 * an auto-signer ever reach the one remaining gate, it lands loudly rather than silently.
 */
type Signer = 'operator' | 'auto-approve' | 'unknown';

function signerOf(gate: { approvedBy?: string | null } | null): Signer {
  if (gate?.approvedBy === 'operator') return 'operator';
  if (gate?.approvedBy === 'auto-approve') return 'auto-approve';
  return 'unknown';
}

/** A date, or nothing. Never a slice of whatever the wire happened to carry. */
function signedOn(at: string | null | undefined): string | null {
  return typeof at === 'string' && at.length >= 10 ? at.slice(0, 10) : null;
}

/*
 * Every read below is defensive. `client.ts` casts responses with `as` and validates nothing, so a
 * backend that drifts on any of these arrays turns a `.map` into a TypeError — and a throw here
 * unmounts the screen that signs the last hard gate.
 */
const componentsOf = (doc: DecisionDoc | null): ComponentDecision[] =>
  doc && Array.isArray(doc.components) ? doc.components.filter((c) => Boolean(c)) : [];
const rowsOf = (c: ComponentDecision): DecisionRow[] => (Array.isArray(c.rows) ? c.rows : []);
const isClear = (r: DecisionRow, k: Criterion): boolean =>
  (r.cleared as unknown as Record<string, unknown> | undefined)?.[k] === true;
const traceOf = (r: DecisionRow, k: Criterion): string =>
  (((r.traceability as unknown as Record<string, unknown> | undefined)?.[OWNER[k].trace] as string) ??
    '') || '—';

/**
 * Sign-off — THE ONLY SIGNATURE LEFT, and therefore the only place the evidence gets read.
 *
 * A signature is a single act, not a workspace you return to, which is why this is its own screen and
 * not the tail of a phase (spec §4). There used to be two of them; the regulatory gate is REMOVED
 * rather than demoted (§16.4), so the VP determination is now the sole human checkpoint before
 * procurement.
 *
 * THAT CHANGES WHAT THIS SCREEN OWES. Removing one gate and leaving the other blind would be worse
 * than either choice alone, so this screen shows the EVIDENCE a judgement needs rather than a
 * summary: which live verdicts nobody has opened, which rest on no citation at all, which the agent
 * itself was unsure of, and which ppm windows sit on a floor nobody measured. The backend enforces
 * the enforceable half — `EvidenceReview.Outstanding` refuses both `POST /decision/determination` and
 * `POST /orders/{cas}` while an unopened live non-Pass verdict exists — and the list here is what
 * turns that refusal from "not armable" into something the operator can act on.
 *
 * THE TRAP: `done` no longer means signed. The pipeline runs end to end with nobody's signature on
 * anything, so every stage can read `done` on a project whose gates are both unsigned, whose export is
 * refused and whose every order is refused. Nothing on this screen is inferred from a stage status.
 *
 * Four things it exists to get right:
 *
 *  1. **A proposal is not a signature.** `proposedCode` is the agent's offer, `confirmedCode` the VP's;
 *     they never share a treatment. And the proposal is HISTORY — once signed it stays on screen beside
 *     the signature, because the audit trail is what the agent said next to what the VP signed.
 *  2. **Armability is the server's word.** `GET /gate/vp` runs the same checks the POST enforces. A
 *     tally assembled here could advertise a pen the POST refuses.
 *  3. **Who signed is a fact, not a default.** An approved gate with no recorded signer reads as
 *     unknown provenance and never as a person.
 *  4. **MSDS-before-order is a hard precondition, stated where the order is placed.** Release itself is
 *     eventually consistent — the pipeline flips procurement by reacting to the approved gate, not by
 *     the signing call — so signing re-reads rather than assuming.
 */
export function Signoff({ project, refreshProject }: ScreenProps) {
  const status = project.stages.decision?.status;

  const [doc, setDoc] = useState<DecisionDoc | null>(null);
  const [vpGate, setVpGate] = useState<VpGateState | null>(null);
  const [dosing, setDosing] = useState<DosingDoc | null>(null);
  /*
   * The project table, read HERE and not only on the phase screens. It is the one projection that
   * carries every verdict's review flag, its citations, its confidence and its ppm floor — which is
   * exactly the evidence §16.4 says this screen must show now that it is the only checkpoint.
   */
  const { state: table } = useProjectTable(project.projectId, status);
  const [sheets, setSheets] = useState<MsdsEntry[]>([]);
  /**
   * How far the MSDS read has got — NOT a boolean, and not derivable from `sheets` being empty. `[]`
   * covers three different situations: not read yet, read and empty, could not be read. Only the middle
   * one may be described as "no sheet on file", and this is the screen that PLACES the order.
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
   * The wire cannot tell us: the rejection branch writes a gate whose status is literally `"locked"`,
   * so *rejected* and *never signed* are the same three bytes, and no `confirmedCode` is written
   * either. Without this flag a successful rejection looks exactly like a no-op. The durable fix is a
   * backend change — `GET /gate/vp` projecting the gate's reason — and a reload loses it until then.
   */
  const [rejectedHere, setRejectedHere] = useState(false);
  /** The `generatedAt` of the doc in state — how a re-read notices the decision changed underneath. */
  const generatedAtRef = useRef<string | null>(null);
  /** A cancellation token for the re-reads that happen OUTSIDE the effect (after a sign or an order). */
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
      // The MSDS read fails INDEPENDENTLY of the project reads: the registry is a CROSS-PROJECT surface
      // that only the order rows need, and a hiccup on it must not replace the decision and the gates
      // with "the decision record could not be read" — a sentence that would also be false.
      const sheetsRead = getMsdsRegistry().then(
        (entries) => ({ ok: true, entries }),
        () => ({ ok: false, entries: [] as MsdsEntry[] }),
      );
      try {
        const [d, vp, dose] = await Promise.all([
          getDecision(project.projectId),
          getVpGate(project.projectId),
          getDosing(project.projectId),
        ]);
        if (signal?.cancelled) return;
        setStaleMsg(null);
        setVpGate(vp);
        setDosing(dose === NotFound ? null : dose);
        if (d === NotFound) {
          setDoc(null);
          generatedAtRef.current = null;
          setPhase('absent');
        } else {
          // The operator's per-component overrides are picks against a SPECIFIC decision. When the
          // decision has been regenerated underneath them, a surviving pick is a choice nobody made
          // about the rows now on screen — and it is still submittable.
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
          // A POST-ACTION re-read. Blanking the screen would unmount the banner explaining WHY the
          // determination was refused — the single most important message on this screen — over a
          // transient failure to re-read. Keep the record and say plainly that it may be stale.
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

  const sign = useCallback(async () => {
    const reason = note.trim();
    if (!reason) {
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
      // The server re-checks and can refuse a button that looked enabled (a concurrent revise). Show
      // its words and re-read the gate for the fresh blockers.
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
      // A recorded ruling consumes its reason: a SECOND ruling needs its own words rather than
      // inheriting the ones already on the record.
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
  const blockers = Array.isArray(vpGate?.blockers) ? vpGate!.blockers : [];

  const vpApproved = vpGate?.status === 'approved';
  const vpSigner: Signer | null = vpApproved ? signerOf(vpGate) : null;
  const vpWhen = vpApproved ? signedOn(vpGate?.approvedAt) : null;

  /**
   * The determination has been made — either the gate says so, or every component carries a signature
   * (both are written by the same endpoint, and either alone is proof one ran).
   *
   * When it has, the pen is WITHDRAWN rather than left on screen disabled: the POST refuses both
   * rulings once the determination exists, and a signing block that keeps drawing a dead "Approve &
   * close project" beside a signed determination invites a second pen at the moment there is none.
   */
  const determined = vpApproved || (components.length > 0 && confirmed === components.length);

  /** The codes dosing actually finalized for a component — the only signable set (the POST 422s others). */
  const finalized = (componentId: string) =>
    codes.filter((k) => k.componentId === componentId).map((k) => k.ratioSignature);

  /**
   * The components whose chosen code the POST would reject: none chosen (`''`), or one that is not in
   * the DosingDoc. This mirrors the endpoint's own membership check rather than guessing at armability
   * — and it can only ever WITHHOLD the pen, never grant it, because arming still requires
   * `gate.armable` from the server.
   */
  const unsignable = confirmations.filter((c) => !finalized(c.componentId).includes(c.code));

  const armable = vpGate?.armable === true;
  const codesReady = components.length > 0 && unsignable.length === 0;
  const noteReady = note.trim().length > 0;
  /*
   * Rejection arms on the server's blockers but NOT on the codes: the endpoint returns from its
   * `rejected` branch before it reads dosing or walks the confirmations. So a component with no
   * signable code blocks an approval and not a rejection, and rejection is the escape hatch on exactly
   * that state.
   */
  const canSign = armable && codesReady && noteReady && busy === null;
  const canReject = armable && noteReady && busy === null;

  return (
    <>
      <section className="screen">
        <SectionHeader
          title="The evidence under this determination"
          headingLevel={3}
          actions={
            /* The compliance package is no longer released by a signature — there is no regulatory
               gate to release it. It is a deliverable, available whenever the record can produce it. */
            <>
              <a className="btn" href={compliancePackageUrl(project.projectId)} download>
                <i className="ti ti-download" aria-hidden="true" /> Compliance package
              </a>
              <a className="btn" href={matrixXlsxUrl(project.projectId)} download>
                <i className="ti ti-download" aria-hidden="true" /> .xlsx
              </a>
            </>
          }
        />
        <Evidence projectId={project.projectId} table={table} dosingProvisional={dosing} />
      </section>

      <section className="screen">
        <SectionHeader
          title="What was decided"
          headingLevel={3}
          count={components.length}
        />

        {malformed && (
          <div className="banner danger" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div className="prose">
              <b>The decision record came back in a shape this screen cannot read.</b> It carries no
              component list, so nothing below can be shown and nothing here can be signed.
            </div>
          </div>
        )}

        {phase === 'absent' && (
          <EmptyState
            icon="ti-gavel"
            title="No decision assembled yet."
          />
        )}

        {phase === 'ready' && !malformed && (
          <>
            {/* Two tiles, and both are things the operator can act on: a blocking row is why the
                determination is not obvious, and an unsigned component is what the approval waits on. */}
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
        <SectionHeader
          title="VP R&D determination"
          headingLevel={3}
        />

        {staleMsg && (
          <div className="banner warn" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div className="prose">
              <b>The record could not be re-read after that action.</b> What is shown is the last good
              read and may now be out of date — reload before acting on it. {staleMsg}
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
          observable moves: the gate stays "locked" and no `confirmedCode` appears — so the block below
          re-renders live and armed, which is correct (the endpoint really does allow a
          re-determination) but reads as "nothing happened".
        */}
        {rejectedHere && !determined && (
          <div className="banner info" role="status">
            <i className="ti ti-ban" aria-hidden="true" />
            <div className="prose">
              <b>The rejection was recorded with your reason; the gate is locked.</b> The block below
              is still live — approving now would supersede it.
            </div>
          </div>
        )}

        {determined ? (
          <VpSignedPanel signer={vpSigner} when={vpWhen} released={released} />
        ) : (
          <SignBlock
            armable={armable}
            gateUnread={vpGate === null}
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

/*
 * `RegulatorySignature` lived here — the second signature, stated on this screen and signed on the
 * Regulatory one. It is deleted with the gate it reported (spec §16.4). Its export link survives, in
 * the section header above, because the compliance package is a deliverable rather than a reward: no
 * signature releases it any more.
 */

/* ---------------------------------------------------------------------------
   The evidence, which is what this screen owes now that it is the only checkpoint.
   --------------------------------------------------------------------------- */

/** One row that needs a person, and the reason it does. `where` is where the person goes. */
interface Finding {
  key: string;
  label: string;
  detail: string;
  where: 'regulatory' | 'dosing';
}

const rowName = (r: ReadRow) => `${r.element} ${r.form} · ${r.cas} on ${r.componentId}`;

/**
 * What the operator has to have looked at, gathered from the one projection that carries all of it.
 *
 * FOUR KINDS, and the first is the one the backend actually refuses over — the other three are
 * §16.4's requirement that this screen show the evidence rather than a summary:
 *
 *   unopened     a LIVE non-Pass verdict nobody opened. `EvidenceReview.Outstanding` refuses the
 *                determination and every order while one exists. Listing them by name is the whole
 *                difference between an actionable refusal and "the gate is not armable".
 *   uncited      a verdict resting on no citation at all — the worst artifact this system produces.
 *   unsure       the agent's own folded confidence below the threshold.
 *   unmeasured   a ppm window whose FLOOR was never measured. That is what `provisional` means now,
 *                and it blocks the order.
 *
 * A dropped row contributes nothing: `stoppedAt` means the row is out, and a substance nobody will
 * ever buy is not evidence anybody needs to read.
 */
function findingsOf(rows: readonly ReadRow[]): {
  unopened: Finding[];
  uncited: Finding[];
  unsure: Finding[];
  unmeasured: Finding[];
} {
  const unopened: Finding[] = [];
  const uncited: Finding[] = [];
  const unsure: Finding[] = [];
  const unmeasured: Finding[] = [];

  for (const r of rows) {
    if (r.stoppedAt) continue;
    const key = `${r.componentId}|${r.cas}`;

    if (r.regulatory.kind === 'cells') {
      const cells = r.regulatory.cells;
      if (cells.overall !== 'Pass' && cells.evidenceReviewed !== true) {
        unopened.push({ key, label: rowName(r), detail: String(cells.overall), where: 'regulatory' });
      }
      if (citationsOf(cells).length === 0) {
        uncited.push({ key, label: rowName(r), detail: 'no citation on any dimension', where: 'regulatory' });
      }
      const confidence = foldConfidence(confidencesOf(cells));
      if (confidence !== null && confidence < LOW_CONFIDENCE) {
        unsure.push({
          key,
          label: rowName(r),
          detail: `${Math.round(confidence * 100)}% at its weakest dimension`,
          where: 'regulatory',
        });
      }
    }

    if (r.dosing.kind === 'cells') {
      const floor: unknown = r.dosing.cells.floor;
      const kind =
        typeof floor === 'object' && floor !== null
          ? (floor as Record<string, unknown>).kind
          : undefined;
      // NOT `!== 'measured'` over a value that may be absent entirely: an unreadable floor is also a
      // floor nobody measured, and it lands here rather than passing silently.
      if (kind !== 'measured') {
        unmeasured.push({
          key,
          label: rowName(r),
          detail: typeof kind === 'string' ? `floor is an ${kind}` : 'the floor could not be read',
          where: 'dosing',
        });
      }
    }
  }

  return { unopened, uncited, unsure, unmeasured };
}

function Evidence({
  projectId,
  table,
  dosingProvisional,
}: {
  projectId: string;
  table: ReturnType<typeof useProjectTable>['state'];
  dosingProvisional: DosingDoc | null;
}) {
  if (table.kind === 'loading') {
    return (
      <p className="small muted" style={{ margin: 0 }}>
        <i className="ti ti-loader" data-running="" aria-hidden="true" /> Reading the record…
      </p>
    );
  }

  if (table.kind === 'error') {
    return (
      /* A failed read is NOT a clean bill of health. This screen signs procurement; silence about
         the evidence must never be shown as an absence of findings. */
      <div className="banner danger" role="alert">
        <i className="ti ti-alert-triangle" aria-hidden="true" />
        <div className="prose">
          <b>The record could not be read, so nothing below is a list of what is outstanding.</b>{' '}
          {table.message}
        </div>
      </div>
    );
  }

  const f = findingsOf(table.read.rows);
  const reasons: string[] = Array.isArray(
    (dosingProvisional as unknown as Record<string, unknown> | null)?.provisionalReasons,
  )
    ? ((dosingProvisional as unknown as Record<string, unknown>).provisionalReasons as unknown[]).filter(
        (r): r is string => typeof r === 'string',
      )
    : [];

  const total = f.unopened.length + f.uncited.length + f.unsure.length + f.unmeasured.length;
  if (total === 0 && reasons.length === 0) {
    return (
      <p className="small" style={{ margin: 0 }}>
        <i className="ti ti-check" style={{ color: 'var(--text-muted)' }} aria-hidden="true" /> Every
        live verdict is opened, cited and confident, and every floor is measured.
      </p>
    );
  }

  return (
    <>
      <FindingList
        projectId={projectId}
        title="Not opened"
        tone="danger"
        /* The one list the SERVER refuses over. It is stated first and loudest because it is the
           difference between a determination that can be recorded and one that cannot. */
        note="the determination and every order are refused while these are unopened"
        items={f.unopened}
      />
      <FindingList projectId={projectId} title="No citation" tone="danger" items={f.uncited} />
      <FindingList projectId={projectId} title="Low confidence" tone="warning" items={f.unsure} />
      <FindingList
        projectId={projectId}
        title="Floor nobody measured"
        tone="warning"
        note="every order is refused over these"
        items={f.unmeasured}
      />
      {reasons.length > 0 && (
        <ul className="small" style={{ margin: 'var(--s2) 0 0', paddingLeft: 18 }}>
          {reasons.map((r) => (
            <li key={r}>{r}</li>
          ))}
        </ul>
      )}
    </>
  );
}

/** One kind of finding, with a way to reach each row. Absent when there are none — never an empty box. */
function FindingList({
  projectId,
  title,
  tone,
  note,
  items,
}: {
  projectId: string;
  title: string;
  tone: 'danger' | 'warning';
  note?: string;
  items: Finding[];
}) {
  if (items.length === 0) return null;
  const colour = tone === 'danger' ? 'var(--text-danger)' : 'var(--text-warning)';
  return (
    <div data-finding={title.toLowerCase().replace(/[^a-z]+/g, '-')} style={{ marginBottom: 'var(--s3)' }}>
      <div className="small" style={{ color: colour, fontWeight: 600 }}>
        {title} <span className="data">{items.length}</span>
        {note && <span style={{ fontWeight: 400 }}> — {note}</span>}
      </div>
      <ul style={{ listStyle: 'none', margin: 'var(--s1) 0 0', padding: 0 }}>
        {items.map((i) => (
          <li key={i.key} className="small" style={{ padding: '2px 0' }}>
            {/* A way to REACH the row, not just its name. A list the operator cannot act on is the
                same refusal they already had, spelled out at greater length. */}
            <Link to={`/p/${projectId}/${i.where}`}>{i.label}</Link>{' '}
            <span className="muted">— {i.detail}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

/**
 * Who signed the VP gate, and when — or, where the record cannot say, in as many lines as it takes.
 *
 * The branches are deliberately not one parameterised box with a `tone`. They differ in what they claim
 * about a person, and that is exactly the difference a shared shell would flatten.
 */
function VpSignedPanel({
  signer,
  when,
  released,
}: {
  signer: Signer | null;
  when: string | null;
  released: boolean;
}) {
  /* Procurement release is eventually consistent: the pipeline flips it by reacting to the approved
     gate, not by the signing call. So an unreleased determination is normal for a few seconds, and this
     screen says so rather than inventing order controls ahead of the record. */
  const tail = released ? null : (
    <>
      {' '}
      Procurement is not released yet — reload in a moment for the order controls.
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
          <b>You recorded VP R&amp;D&rsquo;s determination{when ? ` on ${when}` : ''}.</b> It wrote the
          Marker Library entry and the close conclusion, and released procurement.
          {tail}
        </div>
      </div>
    );
  }

  if (signer === 'unknown' || signer === 'auto-approve') {
    return (
      /*
       * Approved, signer unrecorded. The temptation is to read this as the operator, because in
       * practice it was — and that is precisely why it must not: a gate written before the record named
       * its signer cannot be retroactively claimed as a person's, and the one build that signs this
       * gate without a person is the build whose gates would go unattributed. Withhold.
       */
      <div className="banner warn" data-signer="unknown" role="status">
        <i className="ti ti-help-circle" aria-hidden="true" />
        <div className="prose">
          <b>Approved{when ? ` on ${when}` : ''} &mdash; the record does not say who signed it.</b> The
          Marker Library entry, the close conclusion and the procurement release were all written on
          it. It cannot be read as a person&rsquo;s determination.
          {tail}
        </div>
      </div>
    );
  }

  /*
   * Every component carries a signed code while the gate record does not report an approval — the
   * window between the POST landing and the gate read catching up, and also what a partially written
   * record looks like. The determination is real (the codes above are signed and name their signer) but
   * the gate is not this screen's witness to it, so nothing here is attributed.
   */
  return (
    <div className="banner info" data-signer="components" role="status">
      <i className="ti ti-signature" aria-hidden="true" />
      <div className="prose">
        <b>Every component carries a signed code.</b> The gate record does not report an approval yet,
        so this screen will not say who signed or when &mdash; each component above names its own
        signer.
        {tail}
      </div>
    </div>
  );
}

/**
 * The signature, with its preconditions attached to the buttons rather than floating above them.
 *
 * `armable` is the SERVER's answer and it is what enables the press. The checks EXPLAIN the answer —
 * they do not compute it. A tally assembled in the browser can disagree with the endpoint that is about
 * to refuse it, and the direction it disagrees in is a live-looking button over an unarmed gate.
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
      {/* No paragraph introducing the gate. What each press does is on the press: the buttons are
          labelled with the act, and the line beside them names what is still withheld. */}
      {/* Named, because it is the one list on this screen a reader needs to be able to find: it is what
          stands between the operator and the last hard gate. */}
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
              /* The VP gate's blockers are plain-English sentences meant to be shown verbatim — unlike
                 the regulatory gate's parseable "unreviewed: {cas}|{comp}" strings. */
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
          // "approval only" is in the LABEL, not just the detail: an operator reading an unmet row needs
          // to know there, in the row, that it does not stand between them and a rejection.
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

      {/* One note, for whichever ruling is made. A rejection is a ruling and needs its reason exactly as
          much as an approval does — the backend 422s a blank one either way. */}
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
          {/* THE CONSEQUENCE IS IN THE LABEL. It used to be a section subtitle, which is the shape of
              prose §16.1 deletes — but "a signature described without the act it releases is a chore
              rather than a decision" (§12) still holds, so it moves onto the press itself, where it
              is visible whether or not the gate is armed. A button label is not an explanation. */}
          <i className={`ti ${busy === 'sign' ? 'ti-loader' : 'ti-signature'}`} aria-hidden="true" />{' '}
          {busy === 'sign' ? 'Recording…' : 'Approve — closes the project, releases procurement'}
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
                : 'Writes the Marker Library entry and the close conclusion.'}
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
        role="img"
        aria-label={met ? 'met' : 'not met'}
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
      <SectionHeader eyebrow={c.componentId} count={rows.length} />

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
            indistinguishable from a confirmation, which is the one comparison this record exists to
            make. Muted and labelled — it is a past offer, not a live one.
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
              whose default is the proposal. Printing it as a chip beside the picker as well would put
              the same code on screen twice in two treatments, and the read-only one is exactly the
              shape a signed code takes — which is how a proposal starts looking like a signature.
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
          {/* The wrapping <label> IS the accessible name — one label, not two. */}
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
          recommended ppm, and whether it clears regulatory, dosing and availability.
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
                      <Data kind="cas">{r.cas}</Data>
                    </span>
                  </td>
                  <td className="small">{r.determination}</td>
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
                        {/* The glyph and the `title` carry the meaning visually; neither is announced
                            reliably. Whether a criterion BLOCKS is the cell's content. */}
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
                    <td colSpan={4 + CRITERIA.length} style={{ padding: 0, background: 'var(--surface-2)' }}>
                      <div style={{ borderLeft: '2px solid var(--text-accent)', padding: 'var(--s3)' }}>
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
                              <Link to={`/p/${projectId}/${OWNER[k].slug}`}>
                                {OWNER[k].label} <i className="ti ti-arrow-right" aria-hidden="true" />
                              </Link>{' '}
                              <Data kind="id">{traceOf(r, k)}</Data>
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
