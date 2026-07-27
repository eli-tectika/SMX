import { useEffect, useState } from 'react';
import { NotFound, getPool } from '../../api/client';
import type { PoolDoc } from '../../api/types';
import { CitationChip, SectionHeader } from '../../components/ui/Primitives';

/**
 * The pool agent's output — the first agent to run on a need-only project, and until now the only
 * stage whose result had nowhere to land. Real data from GET /projects/{id}/pool: no MockBadge.
 *
 * It is a HYPOTHESIS, and the copy says so: everything downstream (the XRF filter, Discovery's
 * catalog corroboration, the regulatory screen) is a sieve over it. Presenting it as a finding
 * would invite the operator to trust the least-sieved artifact in the system.
 */
export function ProposedPool({ projectId }: { projectId: string }) {
  const [pool, setPool] = useState<PoolDoc | typeof NotFound | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    getPool(projectId)
      .then((p) => {
        if (!cancelled) setPool(p);
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      });
    return () => {
      cancelled = true;
    };
  }, [projectId]);

  if (error)
    return (
      <div className="tiny" style={{ color: 'var(--text-danger)' }} role="alert">
        <i className="ti ti-alert-triangle" aria-hidden="true" /> {error}
      </div>
    );
  if (pool === null) return <div className="tiny muted">Loading the proposed pool…</div>;
  if (pool === NotFound)
    return <div className="tiny muted">The pool agent has not proposed a pool yet.</div>;

  const components = [...new Set(pool.suggestions.map((s) => s.component))];

  return (
    <section>
      <SectionHeader
        eyebrow="Proposed pool"
        hint="a starting hypothesis — everything downstream sieves it"
      />
      {components.map((component) => (
        <div key={component} style={{ marginBottom: 12 }}>
          <div style={{ fontWeight: 500, fontSize: 13 }}>{component}</div>
          {pool.suggestions
            .filter((s) => s.component === component)
            .map((s) => (
              <div key={`${s.element}-${s.formClass}`} className="step">
                <i className="ti ti-atom" aria-hidden="true" />
                <div>
                  <strong>{s.element}</strong> · {s.formClass}
                  <div className="tiny muted">{s.rationale}</div>
                  <div>
                    {s.citations.map((c, i) => (
                      <CitationChip key={i} {...c} />
                    ))}
                    {/* The pool agent may answer from model knowledge — §9 flags rather than
                        rejects. A flag the operator can see is the whole point; silently
                        rendering it like a cited suggestion is what would mislead. */}
                    {s.uncited && (
                      <span
                        className="src"
                        title="rests on model knowledge, not a retrieved source"
                      >
                        <i className="ti ti-alert-circle" aria-hidden="true" /> no retrieved source
                      </span>
                    )}
                  </div>
                </div>
              </div>
            ))}
        </div>
      ))}
    </section>
  );
}
