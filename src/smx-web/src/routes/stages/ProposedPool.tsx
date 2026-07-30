import type { ReactNode } from 'react';
import { NotFound, getPool } from '../../api/client';
import { usePolling } from '../../hooks/usePolling';
import { CitationList, SectionHeader } from '../../components/ui/Primitives';

/**
 * The per-component group heading, shared by the two screens that group by component (this file
 * and Discovery, which already imports this module — so the dependency runs one way and there is
 * no cycle). It lives here rather than in a file of its own because the two headings must not
 * drift: a product decomposes into components, and background, form, ppm and codes all run
 * independently per component, so the component grouping is the load-bearing structure of both
 * screens.
 *
 * Why not `SectionHeader`: its title is weight 500, and every item BELOW it on these screens — an
 * element symbol, a candidate's name — is semibold. A heading lighter than its own children
 * inverts the hierarchy, and measured on the deployed app that is exactly what happened (`bottle`
 * at 13px/500 above `Ce` at 12px/600). Here the heading is the larger and heavier of the two, in
 * the brand face and ink, and a hairline closes it — so a group reads as a container rather than
 * as a caption on the first item in it.
 */
export function ComponentHeading({
  component,
  count,
  hint,
  level = 4,
}: {
  component: string;
  /** The one hint the record actually carries per component: how many rows are on this track. */
  count?: number;
  hint?: ReactNode;
  level?: 3 | 4 | 5;
}) {
  const Heading = `h${level}` as 'h4';
  return (
    <div
      data-component-heading=""
      style={{
        display: 'flex',
        alignItems: 'baseline',
        gap: 'var(--s2)',
        flexWrap: 'wrap',
        borderBottom: 'var(--hair) solid var(--border-strong)',
        paddingBottom: 'var(--s2)',
        marginBottom: 'var(--s2)',
      }}
    >
      <Heading
        style={{
          margin: 0,
          fontFamily: 'var(--font-serif)',
          fontSize: 'var(--t-lead)',
          fontWeight: 'var(--w-semibold)',
          color: 'var(--brand-navy)',
        }}
      >
        {component}
      </Heading>
      {count !== undefined && <span className="sec__count">{count}</span>}
      {hint && <span className="sec__hint">{hint}</span>}
    </div>
  );
}

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
 * `hint` lets each host say why the pool matters THERE: it is the element list Background measures
 * against, and the hypothesis Discovery is corroborating.
 *
 * `heading` may be turned off by a host whose own section already names the pool — two headings
 * reading "The proposed pool" and "PROPOSED POOL" in consecutive lines is noise, not hierarchy.
 * It is opt-out rather than opt-in because a bare list of elements with nothing saying what it is
 * would be the worse default.
 */
export function ProposedPool({
  projectId,
  hint,
  heading = true,
}: {
  projectId: string;
  hint?: string;
  heading?: boolean;
}) {
  const state = usePolling(
    () => getPool(projectId),
    (p) => p !== NotFound,
    [projectId],
  );

  /* READ, not chrome: a failure the operator has to parse before they can do anything about it.
     `.prose` sets primary ink, so the danger colour is restated inline — an inline style is the
     one thing that still beats a class, and the sentence keeps saying that it failed. */
  if (state.kind === 'error')
    return (
      <div className="prose" style={{ color: 'var(--text-danger)' }} role="alert">
        <i className="ti ti-alert-triangle" aria-hidden="true" /> {state.message}
      </div>
    );
  /* REFERENCED: a transient status label, glanced at and gone the moment the poll lands. */
  if (state.kind === 'loading') return <div className="tiny muted">Loading the proposed pool…</div>;
  const pool = state.data;
  if (pool === NotFound)
    return (
      <div className="prose">
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
      <div className="prose" role="alert">
        <i className="ti ti-alert-triangle" aria-hidden="true" /> Could not read the proposed pool.
      </div>
    );

  const components = [...new Set(pool.suggestions.map((s) => s.component))];

  return (
    <section>
      {heading && (
        <SectionHeader
          eyebrow="Proposed pool"
          hint={hint ?? 'a starting hypothesis — everything downstream sieves it'}
        />
      )}
      {components.map((component) => {
        const forComponent = pool.suggestions.filter((s) => s.component === component);
        return (
          <div key={component} style={{ marginBottom: 'var(--s4)' }}>
            <ComponentHeading component={component} count={forComponent.length} />
            {forComponent.map((s, i) => (
              <div
                key={`${s.element}-${s.formClass}`}
                className="step"
                data-suggestion=""
                style={{
                  /* One hairline between neighbours, none above the first — its group heading's
                     own rule is already the line above it. Without this the suggestions ran
                     together as a single wall of symbol / prose / chips with nothing marking
                     where one element ended and the next began. */
                  borderTop: i === 0 ? undefined : 'var(--hair) solid var(--border)',
                  padding: 'var(--s3) 0',
                }}
              >
                <i className="ti ti-atom" aria-hidden="true" />
                <div>
                  {/* REFERENCED — an element symbol and a form class are identified at a glance,
                      not parsed. Sized one step under its group heading and one step over the
                      chrome, so the item still outranks the citations beneath it. */}
                  <div
                    data-suggestion-title=""
                    style={{
                      fontSize: 'var(--t-read)',
                      fontWeight: 'var(--w-semibold)',
                      color: 'var(--text-primary)',
                    }}
                  >
                    <strong>{s.element}</strong> · {s.formClass}
                  </div>
                  {/* READ — the agent's reasoning for proposing this element at all, and the only
                      thing in this block that is a sentence. It was `tiny muted`: the smallest,
                      lowest-contrast text in the region, carrying its highest-value content. */}
                  <div className="prose">{s.rationale}</div>
                  <div>
                    <CitationList citations={s.citations} />
                    {/* The pool agent may answer from model knowledge — §9 flags rather than
                        rejects. A flag the operator can see is the whole point; silently
                        rendering it like a cited suggestion is what would mislead. */}
                    {s.uncited && (
                      <span className="src" title="rests on model knowledge, not a retrieved source">
                        <i className="ti ti-alert-circle" aria-hidden="true" /> no retrieved source
                      </span>
                    )}
                  </div>
                </div>
              </div>
            ))}
          </div>
        );
      })}
    </section>
  );
}
