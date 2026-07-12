import { useState } from 'react';
import type { ProjectSummary } from '../../api/types';
import { MockBadge } from '../../components/MockBadge';
import { StageStatusCard } from '../../components/StageStatusCard';
import { BarRow, CitationChip } from '../../components/ui/Primitives';
import discovery from '../../mocks/fixtures/discovery.json';

/** Tier IS a severity ordering — strong / needs-validation / excluded — so the verdict palette fits. */
const TIER_CLASS: Record<string, string> = { A: 'v', B: 'l', C: 'x' };
const TIER_BG: Record<string, string> = {
  A: 'var(--text-success)',
  B: 'var(--text-pro)',
  C: 'var(--text-danger)',
};

interface Candidate {
  element: string;
  form: string;
  cas: string;
  metalPercent: number;
  why: string;
  sources: string[];
}
interface Tier {
  tier: string;
  candidates: Candidate[];
}

/**
 * Discovery & AI-screening (spec §4.3).
 *
 * The screening stage's *status* is real — it comes from ProjectDoc.stages.screening.
 * The A/B/C candidate tiers are fixtures: the record persists verdict docs, not a
 * ranked pool, and exposes no endpoint for one.
 *
 * Spec §4.3 calls this "the heaviest provenance burden" — the one stage of open-ended
 * search — so every candidate carries why-this-tier and its sources.
 */
export function Discovery({ project }: { project: ProjectSummary }) {
  const [openTier, setOpenTier] = useState<string | null>('A');
  const { queries, tiers } = discovery as { queries: string[]; tiers: Tier[] };

  const total = tiers.reduce((n, t) => n + t.candidates.length, 0);
  const maxMetal = Math.max(...tiers.flatMap((t) => t.candidates.map((c) => c.metalPercent)));

  return (
    <section className="screen" data-provenance="mock">
      <div className="cap">
        <b>Discovery &amp; AI-screening</b> &nbsp;·&nbsp; spec §4.3 — candidates + regulatory
        pre-checks
      </div>

      <StageStatusCard name="Screening agent" state={project.stages.screening} />

      <MockBadge note="The screening status above is real. The candidate tiers below are not — the record stores verdicts, not a ranked pool." />

      {/* The tier shape, without having to open an accordion to learn anything. */}
      <div style={{ marginBottom: 6 }}>
        <div className="ribbon" role="img" aria-label={tiers.map((t) => `${t.candidates.length} tier ${t.tier}`).join(', ')}>
          {tiers.map((t) =>
            t.candidates.length ? (
              <div
                key={t.tier}
                className="ribbon__seg"
                style={{ width: `${(t.candidates.length / total) * 100}%`, background: TIER_BG[t.tier] }}
                title={`${t.candidates.length} in tier ${t.tier}`}
              />
            ) : null,
          )}
        </div>
        <div className="ribbon__key">
          {tiers.map((t) => (
            <span key={t.tier}>
              <span className="ribbon__dot" style={{ background: TIER_BG[t.tier] }} />
              {t.candidates.length} tier {t.tier}
            </span>
          ))}
        </div>
      </div>

      <div style={{ margin: '14px 0' }}>
        {queries.map((q) => (
          <span className="src" key={q}>
            <i className="ti ti-search" aria-hidden="true" /> {q}
          </span>
        ))}
      </div>

      {tiers.map((t) => {
        const isOpen = openTier === t.tier;
        return (
          <div className="card" key={t.tier} style={{ marginBottom: 10 }}>
            <button
              type="button"
              onClick={() => setOpenTier(isOpen ? null : t.tier)}
              aria-expanded={isOpen}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 8,
                width: '100%',
                background: 'none',
                border: 0,
                padding: 0,
                cursor: 'pointer',
                textAlign: 'left',
              }}
            >
              <span className={`chip ${TIER_CLASS[t.tier]}`}>{t.tier}</span>
              <span style={{ fontSize: 12, fontWeight: 500 }}>
                tier {t.tier} · {t.candidates.length} candidate
                {t.candidates.length === 1 ? '' : 's'}
              </span>
              <i
                className={`ti ${isOpen ? 'ti-chevron-up' : 'ti-chevron-down'}`}
                style={{ marginLeft: 'auto', color: 'var(--text-muted)' }}
                aria-hidden="true"
              />
            </button>

            {isOpen &&
              t.candidates.map((c) => (
                <div
                  key={c.cas}
                  style={{ borderTop: '0.5px solid var(--border)', marginTop: 12, paddingTop: 12 }}
                >
                  <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
                    <span style={{ fontSize: 13, fontWeight: 500 }}>
                      {c.element} {c.form}
                    </span>
                    <span className="tiny muted" style={{ fontFamily: 'var(--font-mono)' }}>
                      CAS {c.cas}
                    </span>
                  </div>

                  <div style={{ maxWidth: 300, margin: '6px 0' }}>
                    <BarRow
                      label={<span className="tiny muted">metal loading</span>}
                      value={c.metalPercent}
                      max={maxMetal}
                      display={`${c.metalPercent}%`}
                    />
                  </div>

                  <p className="small secondary" style={{ margin: '4px 0 4px' }}>
                    {c.why}
                  </p>

                  <div>
                    {c.sources.map((s) => (
                      <CitationChip
                        key={s}
                        source={s}
                        reference="catalog"
                        retrievedAt="2026-07-01T00:00:00Z"
                      />
                    ))}
                    <span className="tiny muted" style={{ marginLeft: 4 }}>
                      {c.sources.length} independent source{c.sources.length === 1 ? '' : 's'}
                    </span>
                  </div>

                  {/*
                    These used to read "Move to B" / "Exclude", which implies the operator
                    can hand-mutate the agent's record. Spec §1.4 and §4.3 forbid exactly
                    that: no manual re-tiering — you instruct the agent WITH A REASON, and
                    the reason is recorded as a Learned Conclusion. The label is the
                    contract, so the label had to change.
                  */}
                  <div style={{ marginTop: 8 }}>
                    <button
                      className="qr"
                      disabled
                      title="Disabled — no agent endpoint. Spec §1.4: re-tiering goes through the agent with a reason, never by hand."
                    >
                      <i className="ti ti-message-2" aria-hidden="true" /> Ask the agent to re-tier
                      (needs a reason)
                    </button>
                  </div>
                </div>
              ))}
          </div>
        );
      })}
    </section>
  );
}
