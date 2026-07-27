import { useCallback, useEffect, useState } from 'react';
import { NotFound, getCandidates } from '../../api/client';
import type { CandidateSubstance, CandidatesDoc } from '../../api/types';
import { Loading } from '../../components/Loading';
import { ReviseForm, RevisionTrail } from '../../components/RevisionControls';
import { StageStatusCard } from '../../components/StageStatusCard';
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

/**
 * Discovery & AI-screening (spec §4.3) — real.
 *
 * Three things this screen used to get wrong, and now cannot:
 *
 *  1. **Candidates are per-component tracks.** The fixture flattened them into one product-wide
 *     ranked pool, which contradicts the architecture — background, form, ppm and codes all run
 *     independently per component. Grouping is by component first, tier second.
 *  2. **Citations are the agent's, verbatim.** The fixture passed `reference="catalog"` and a
 *     fabricated `retrievedAt` to every chip. A citation without its retrieval date is a claim.
 *  3. **Nothing is shown that the record does not hold.** The search queries and the metal-loading
 *     bars are gone: Discovery never persists its queries, and no metal-loading figure exists
 *     anywhere in the record. Drawing either was inventing evidence for the one stage the spec
 *     calls "the heaviest provenance burden".
 *
 * `preferred` and the tier cap are the deterministic rails, surfaced: a web-only candidate is capped
 * at tier B and can never be preferred (DiscoveryAgent.Validate), so a preferred row is a claim about
 * corpus evidence.
 */
export function Discovery({ project }: ScreenProps) {
  const stage = project.stages.discovery;
  const status = stage?.status;

  const [doc, setDoc] = useState<CandidatesDoc | null>(null);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [errMsg, setErrMsg] = useState<string>();
  const [reviseNonce, setReviseNonce] = useState(0);

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

  const substances = doc?.substances ?? [];
  const total = substances.length;

  return (
    <section className="screen">
      <div className="cap">
        <b>Discovery &amp; AI-screening</b>
        Candidates + regulatory pre-checks, per component
      </div>

      <StageStatusCard name="Discovery agent" state={stage} />

      {/* The pool is Discovery's INPUT, and Discovery takes minutes. Without this the operator
          watches an empty candidate list for the whole run with no way to see what is being
          screened — the pool is the one real thing there is to show in that window. */}
      <ProposedPool
        projectId={project.projectId}
        hint="what Discovery is corroborating against the catalog"
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
              Until then there is nothing to rank — the stage status above says where it is.
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
                eyebrow={component}
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
                <CandidateCard
                  key={`${c.componentId}|${c.cas}`}
                  candidate={c}
                  projectId={project.projectId}
                  onRevised={() => setReviseNonce((n) => n + 1)}
                />
              ))}
            </div>
          );
        })}

      <RevisionTrail projectId={project.projectId} refreshKey={reviseNonce} />
    </section>
  );
}

function CandidateCard({
  candidate: c,
  projectId,
  onRevised,
}: {
  candidate: CandidateSubstance;
  projectId: string;
  onRevised: () => void;
}) {
  return (
    <div className="card" style={{ marginBottom: 8 }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
        <span className={`chip ${TIER_CLASS[c.tier]}`}>{c.tier}</span>
        <span style={{ fontSize: 13, fontWeight: 500 }}>
          {c.element} {c.form}
        </span>
        <span className="tiny muted">
          CAS <Data kind="code">{c.cas}</Data>
        </span>
        {/* Preferred is the agent's pick, and it is unreachable for a web-only candidate — so it
            says something about the evidence, not just about the ranking. */}
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

      <p className="small secondary" style={{ margin: '6px 0 4px' }}>
        {c.rationale}
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
        No manual re-tiering (spec §1.4 / §4.3): the operator never hand-mutates the agent's record.
        This is the real path — name the candidate, state the reason, and the agent applies the change
        and records the reason as a Learned Conclusion.
      */}
      <div style={{ marginTop: 8 }}>
        <ReviseForm
          projectId={projectId}
          stage="discovery"
          fixedTarget={`${c.element} ${c.form}`}
          onRequested={onRevised}
        />
      </div>
    </div>
  );
}
