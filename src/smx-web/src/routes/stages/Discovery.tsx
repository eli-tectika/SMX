import { useState } from 'react';
import type { ProjectSummary } from '../../api/types';
import { MockBadge } from '../../components/MockBadge';
import { StageStatusCard } from '../../components/StageStatusCard';
import discovery from '../../mocks/fixtures/discovery.json';

const TIER_CLASS: Record<string, string> = { A: 'v', B: 'l', C: 'x' };

/**
 * Discovery & AI-screening (spec §4.3).
 *
 * The screening stage's *status* is real — it comes from ProjectDoc.stages.screening.
 * The A/B/C candidate tiers below are fixtures: the backend persists verdict docs, not
 * a ranked candidate pool, and exposes no endpoint for one.
 */
export function Discovery({ project }: { project: ProjectSummary }) {
  const [openTier, setOpenTier] = useState<string | null>('A');
  const { queries, tiers } = discovery as {
    queries: string[];
    tiers: {
      tier: string;
      candidates: {
        element: string;
        form: string;
        cas: string;
        metalPercent: number;
        why: string;
        sources: string[];
      }[];
    }[];
  };

  return (
    <section className="screen">
      <div className="cap">
        <b>Discovery &amp; AI-screening</b> &nbsp;·&nbsp; spec §4.3 — candidates + regulatory
        pre-checks
      </div>

      <StageStatusCard name="Screening agent" state={project.stages.screening} />

      <MockBadge note="The screening status above is real. The candidate tiers below are not — the record stores verdicts, not a ranked pool." />

      <div style={{ marginBottom: 14 }}>
        {queries.map((q) => (
          <span className="src" key={q}>
            <i className="ti ti-search" aria-hidden="true" /> {q}
          </span>
        ))}
      </div>

      {tiers.map((t) => (
        <div className="region" key={t.tier} style={{ marginBottom: 10 }}>
          <button
            type="button"
            onClick={() => setOpenTier(openTier === t.tier ? null : t.tier)}
            aria-expanded={openTier === t.tier}
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
              className={`ti ${openTier === t.tier ? 'ti-chevron-up' : 'ti-chevron-down'}`}
              style={{ marginLeft: 'auto', color: 'var(--text-muted)' }}
              aria-hidden="true"
            />
          </button>

          {openTier === t.tier &&
            t.candidates.map((c) => (
              <div
                key={c.cas}
                style={{ borderTop: '0.5px solid var(--border)', marginTop: 10, paddingTop: 10 }}
              >
                <div style={{ display: 'flex', alignItems: 'baseline', gap: 8 }}>
                  <span style={{ fontSize: 13, fontWeight: 500 }}>
                    {c.element} {c.form}
                  </span>
                  <span className="tiny muted" style={{ fontFamily: 'ui-monospace, monospace' }}>
                    CAS {c.cas}
                  </span>
                  <span className="tiny muted" style={{ marginLeft: 'auto' }}>
                    {c.metalPercent}% metal
                  </span>
                </div>
                <p className="small secondary" style={{ margin: '5px 0 3px' }}>
                  {c.why}
                </p>
                <div>
                  {c.sources.map((s) => (
                    <span className="src" key={s}>
                      {s}
                    </span>
                  ))}
                </div>
                <div style={{ marginTop: 6 }}>
                  <button className="qr" disabled title="Disabled — no discovery endpoint">
                    Move to B
                  </button>
                  <button className="qr" disabled title="Disabled — no discovery endpoint">
                    Exclude
                  </button>
                </div>
              </div>
            ))}
        </div>
      ))}
    </section>
  );
}
