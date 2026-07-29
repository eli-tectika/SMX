import { useCallback, useEffect, useState } from 'react';
import { NotFound, getCandidates } from '../../api/client';
import type { CandidateSubstance, CandidatesDoc, Citation } from '../../api/types';
import { Loading } from '../../components/Loading';
import { RevisionTrail } from '../../components/RevisionControls';
import { ProposedPool } from './ProposedPool';
import { Data } from '../../components/ui/Data';
import { CitationChip, EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { byComponent } from '../../domain/dosing';
import type { ScreenProps } from '../ProjectLayout';

/** Tier IS a severity ordering — strong / needs-validation / excluded — so the verdict palette fits. */
const TIER_CLASS: Record<string, string> = { A: 'v', B: 'l', C: 'x' };
const TIER_BG: Record<string, string> = {
  A: 'var(--text-success)',
  B: 'var(--text-pro)',
  C: 'var(--text-danger)',
};
const TIERS = ['A', 'B', 'C'] as const;
const TIER_SET = new Set<string>(TIERS);

/**
 * The docked chat's composer, on this screen, always names the discovery agent — `discovery` has
 * exactly one backing stage (domain/stages.ts), so AgentPanel never shows a tab strip here and this
 * label never varies.
 *
 * There is no shared store between a stage screen and the permanent chat column (ProjectLayout mounts
 * them side by side, not through each other), and this file may not add one — so this is the simple
 * mechanism asked for: find the composer input that is ALREADY in the page next to this screen and
 * drive it the way a user would. Setting `.value` through the native `HTMLInputElement` setter
 * (bypassing the instance setter React installs to track a controlled input) and then dispatching a
 * real `input` event is what makes React's own `onChange` fire — a plain `input.value = …` would only
 * change the pixel, not the composer's state, and the very next keystroke would wipe it. If the
 * composer is not mounted (a screen rendered outside `ProjectLayout`, as in this file's own tests)
 * this is a silent no-op, not a throw.
 */
const DISCOVERY_COMPOSER_LABEL = 'Message the discovery agent';

function focusChatWithTarget(target: string) {
  const input = document.querySelector<HTMLInputElement>(`[aria-label="${DISCOVERY_COMPOSER_LABEL}"]`);
  if (!input) return;
  const nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;
  const prefill = `Revise ${target}: `;
  nativeSetter?.call(input, prefill);
  input.dispatchEvent(new Event('input', { bubbles: true }));
  input.focus();
  input.setSelectionRange(prefill.length, prefill.length);
}

function normalizeCitation(raw: unknown): Citation | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  if (typeof r.source !== 'string' || typeof r.reference !== 'string' || typeof r.retrievedAt !== 'string') {
    return null;
  }
  return {
    source: r.source,
    reference: r.reference,
    retrievedAt: r.retrievedAt,
    snippet: typeof r.snippet === 'string' ? r.snippet : undefined,
  };
}

/**
 * Defensive, not decorative: a candidate that arrived without a component, a CAS or a valid tier is
 * not something this screen can show without inventing the missing piece, so it is dropped rather
 * than rendered half-formed or allowed to throw `byComponent` (which assumes `componentId` on every
 * row) or `CandidateCard` (which assumes `citations` is an array).
 */
function normalizeCandidate(raw: unknown): CandidateSubstance | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  if (typeof r.componentId !== 'string' || typeof r.cas !== 'string') return null;
  return {
    componentId: r.componentId,
    element: typeof r.element === 'string' ? r.element : '?',
    form: typeof r.form === 'string' ? r.form : '?',
    cas: r.cas,
    particleSize: typeof r.particleSize === 'string' ? r.particleSize : undefined,
    solvent: typeof r.solvent === 'string' ? r.solvent : undefined,
    preferred: r.preferred === true,
    tier: TIER_SET.has(r.tier as string) ? (r.tier as CandidateSubstance['tier']) : 'C',
    rationale: typeof r.rationale === 'string' ? r.rationale : '',
    citations: Array.isArray(r.citations)
      ? r.citations.map(normalizeCitation).filter((c): c is Citation => c !== null)
      : [],
  };
}

/**
 * Discovery & AI-screening — the candidate pool, read per component.
 *
 * Three things this screen used to get wrong, and now cannot:
 *
 *  1. **Candidates are per-component tracks.** Grouping is by component first, tier second — never
 *     flattened into one product-wide ranked pool, which would contradict the architecture:
 *     background, form, ppm and codes all run independently per component.
 *  2. **Citations are the agent's, verbatim.** A citation without its retrieval date is a claim, not
 *     a source, so the real `source`/`reference`/`retrievedAt` reach the chip unmodified.
 *  3. **Nothing is shown that the record does not hold.** No search queries, no metal-loading bars —
 *     Discovery never persists either, and drawing them would be inventing evidence for the stage
 *     with the heaviest provenance burden.
 *
 * `preferred` and the tier cap are the deterministic rails, surfaced: a web-only candidate is capped
 * at tier B and can never be preferred (DiscoveryAgent.Validate), so a preferred row is a claim about
 * corpus evidence — never something this screen infers from tier or ranking.
 *
 * No per-card form any more. The agent now lives in a permanent left column for the whole stage, so
 * "no direct edits — instruct the agent with a reason" is a conversation there, not a form buried at
 * the foot of a card: each card's button only focuses that composer with the candidate named, and the
 * operator finishes the sentence — types why — and sends it like any other message.
 */
export function Discovery({ project }: ScreenProps) {
  const stage = project.stages.discovery;
  const status = stage?.status;

  const [doc, setDoc] = useState<CandidatesDoc | null>(null);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [errMsg, setErrMsg] = useState<string>();

  const load = useCallback(
    async (signal?: { cancelled: boolean }) => {
      try {
        const res = await getCandidates(project.projectId);
        if (signal?.cancelled) return;
        if (res === NotFound) {
          setDoc(null);
          setPhase('absent');
        } else {
          setDoc(res);
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

  if (phase === 'loading') return <Loading what="the candidate pool" />;

  const rawSubstances = Array.isArray(doc?.substances) ? doc.substances : [];
  const substances = rawSubstances
    .map(normalizeCandidate)
    .filter((c): c is CandidateSubstance => c !== null);
  const total = substances.length;

  return (
    <section className="screen">
      {/* The pool is Discovery's INPUT, and Discovery takes minutes. Without this the operator
          watches an empty candidate list for the whole run with no way to see what is being
          screened — the pool is the one real thing there is to show in that window. */}
      <ProposedPool
        projectId={project.projectId}
        hint="what Discovery is corroborating against the catalog"
      />

      <SectionHeader
        title="Candidates"
        headingLevel={3}
        count={phase === 'ready' ? total : undefined}
        hint="grouped by component — there is no product-wide pool"
      />

      {phase === 'error' && (
        <div className="banner warn" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The candidate pool could not be read.</b>
            <div className="tiny" style={{ marginTop: 3 }}>{errMsg}</div>
          </div>
        </div>
      )}

      {phase === 'absent' && (
        <EmptyState
          icon="ti-flask-off"
          title="No candidates yet."
          body={
            <>
              Discovery writes its pool once it has screened the element pool against the catalog.
              Until then there is nothing to rank.
            </>
          }
        />
      )}

      {phase === 'ready' && total === 0 && (
        <EmptyState
          icon="ti-flask-off"
          title="Discovery found no candidates."
          body={
            <>
              The agent ran and produced an empty pool. That is a finding, not a gap: nothing in the
              catalog matched this project's element pool.
            </>
          }
        />
      )}

      {phase === 'ready' &&
        byComponent(substances).map(([component, rows]) => {
          // Stable sort: within a tier the agent's own order is a ranking it chose, and the UI
          // must not re-rank it. Only the tier bucket (A before B before C) is imposed here, so the
          // ribbon above (drawn A/B/C left to right) and the card list below are never out of step.
          // `.slice()` first — byComponent hands back the arrays it built internally, and sorting
          // one in place would mutate its return value.
          const forComponent = rows.slice().sort((a, b) => TIERS.indexOf(a.tier) - TIERS.indexOf(b.tier));
          return (
            <div key={component} style={{ marginBottom: 18 }}>
              <SectionHeader
                title={component}
                headingLevel={4}
                count={forComponent.length}
                hint="candidates on this component's own track"
              />

              {/* The tier shape, without having to open anything to learn it. */}
              <div style={{ marginBottom: 10 }}>
                <div
                  className="ribbon"
                  role="img"
                  aria-label={TIERS.map(
                    (t) => `${forComponent.filter((s) => s.tier === t).length} tier ${t}`,
                  ).join(', ')}
                >
                  {TIERS.map((t) => {
                    const n = forComponent.filter((s) => s.tier === t).length;
                    return n ? (
                      <div
                        key={t}
                        className="ribbon__seg"
                        style={{ width: `${(n / forComponent.length) * 100}%`, background: TIER_BG[t] }}
                        title={`${n} in tier ${t}`}
                      />
                    ) : null;
                  })}
                </div>
                <div className="ribbon__key">
                  {TIERS.map((t) => (
                    <span key={t}>
                      <span className="ribbon__dot" style={{ background: TIER_BG[t] }} />
                      {forComponent.filter((s) => s.tier === t).length} tier {t}
                    </span>
                  ))}
                </div>
              </div>

              {forComponent.map((c) => (
                <CandidateCard key={`${c.componentId}|${c.cas}`} candidate={c} />
              ))}
            </div>
          );
        })}

      <details style={{ marginTop: 'var(--s4)' }}>
        <summary className="small secondary" style={{ cursor: 'pointer' }}>
          Revision trail
        </summary>
        <RevisionTrail projectId={project.projectId} />
      </details>
    </section>
  );
}

function CandidateCard({ candidate: c }: { candidate: CandidateSubstance }) {
  return (
    <div className="card" style={{ marginBottom: 8 }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, flexWrap: 'wrap' }}>
        {/* Tier is the loud element: the verdict palette, at lead size. */}
        <span
          className={`chip ${TIER_CLASS[c.tier]}`}
          style={{ fontSize: 'var(--t-lead)', fontWeight: 'var(--w-semibold)', height: 'auto', padding: '2px 9px' }}
        >
          {c.tier}
        </span>
        <span style={{ fontSize: 'var(--t-lead)', fontWeight: 'var(--w-semibold)' }}>
          {c.element} {c.form}
        </span>
        <span className="tiny muted">
          CAS <Data kind="code">{c.cas}</Data>
        </span>
        {/* Preferred is the agent's pick, and it is unreachable for a web-only candidate — so it
            says something about the evidence, not just about the ranking. Read straight off the
            record's own flag: never inferred here from tier or from list position. */}
        {c.preferred && (
          <span className="chip chip--neutral" title="The agent's preferred candidate on this component">
            preferred
          </span>
        )}
      </div>

      {(c.particleSize || c.solvent) && (
        <div className="tiny muted" style={{ marginTop: 4 }}>
          {c.particleSize && <>particle size {c.particleSize}</>}
          {c.particleSize && c.solvent && ' · '}
          {c.solvent && <>solvent {c.solvent}</>}
        </div>
      )}

      <p className="prose" style={{ margin: '8px 0 6px' }}>
        {c.rationale || <span className="muted">No rationale recorded.</span>}
      </p>

      <div>
        {c.citations.map((cite) => (
          <CitationChip
            key={`${cite.source}|${cite.reference}`}
            source={cite.source}
            reference={cite.reference}
            retrievedAt={cite.retrievedAt}
            snippet={cite.snippet}
          />
        ))}
        <span className="tiny muted" style={{ marginLeft: 4 }}>
          {c.citations.length} source{c.citations.length === 1 ? '' : 's'}
        </span>
      </div>

      {/*
        No manual re-tiering (Law 4): the operator never hand-mutates the agent's record. This
        button does not itself change anything — it only focuses the docked chat with this candidate
        named, so the real path (name the candidate, state the reason, the agent applies the change
        and records the reason as a Learned Conclusion) happens as a normal message, not a form.
      */}
      <div style={{ marginTop: 10 }}>
        <button
          type="button"
          className="btn"
          aria-label={`Revise ${c.element} ${c.form} in chat`}
          onClick={() => focusChatWithTarget(`${c.element} ${c.form}`)}
        >
          <i className="ti ti-message-2" aria-hidden="true" /> Revise in chat
        </button>
      </div>
    </div>
  );
}
