import { NotFound, getPool } from '../../api/client';
import { usePolling } from '../../hooks/usePolling';
import { CitationChip, SectionHeader } from '../../components/ui/Primitives';

/**
 * The pool agent's output. Real data from GET /projects/{id}/pool.
 *
 * It is a HYPOTHESIS, and the copy says so: everything downstream (the XRF filter, Discovery's
 * catalog corroboration, the regulatory screen) is a sieve over it. Presenting it as a finding
 * would invite the operator to trust the least-sieved artifact in the system.
 *
 * POLLED, not read once. The pool takes about a minute to produce, so an operator watching any of
 * the screens this appears on is usually watching it from BEFORE it exists — and a single read
 * would 404, print "not proposed yet", and leave that sentence up permanently while the pool sat
 * finished on the server. The loop stops the moment the doc lands, so a settled project pays
 * nothing.
 *
 * `heading` may be turned off by a host whose own section already names the pool — two headings
 * reading "The proposed pool" and "PROPOSED POOL" in consecutive lines is noise, not hierarchy.
 * It is opt-out rather than opt-in because a bare list of elements with nothing saying what it is
 * would be the worse default.
 *
 * The `hint` prop is GONE. It existed so each host could explain what the pool was for, and a
 * sentence explaining how the app works is a defect (spec §16.1) — the eyebrow names the artifact
 * and the rows say the rest.
 */
export function ProposedPool({
  projectId,
  heading = true,
}: {
  projectId: string;
  heading?: boolean;
}) {
  const state = usePolling(
    () => getPool(projectId),
    (p) => p !== NotFound,
    [projectId],
  );

  if (state.kind === 'error')
    return (
      <div className="tiny" style={{ color: 'var(--text-danger)' }} role="alert">
        <i className="ti ti-alert-triangle" aria-hidden="true" /> {state.message}
      </div>
    );
  if (state.kind === 'loading') return <div className="tiny muted">Loading the proposed pool…</div>;
  const pool = state.data;
  if (pool === NotFound)
    return (
      <div className="tiny muted">
        <i className="ti ti-loader" data-running="" aria-hidden="true" /> The pool agent has not
        proposed a pool yet — this fills in as soon as it does.
      </div>
    );

  /*
   * `GET /projects/{id}/pool` has been observed to come back list-shaped instead of a PoolDoc —
   * a bare `[]` in place of `{ projectId, suggestions: [] }` — which used to call `.map` on
   * `undefined` here and, uncaught, unmount the whole project shell around this one hint. This is
   * the one runtime shape check in this file, not a general schema-validation layer: it exists to
   * keep a malformed pool confined to its own region instead of taking the screen down with it.
   */
  if (!Array.isArray(pool.suggestions))
    return (
      <div className="tiny muted" role="alert">
        <i className="ti ti-alert-triangle" aria-hidden="true" /> Could not read the proposed pool.
      </div>
    );

  const components = [...new Set(pool.suggestions.map((s) => s.component))];

  return (
    <section>
      {heading && <SectionHeader eyebrow="Proposed pool" />}
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
