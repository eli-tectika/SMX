import { useState } from 'react';
import type { DiscoveryCells } from '../../api/types';
import { Loading } from '../../components/Loading';
import { RevisionTrail } from '../../components/RevisionControls';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { ProposedPool } from './ProposedPool';
import {
  AbsentCells,
  DroppedRows,
  IdentityCell,
  TableError,
  byComponentRows,
  useProjectTable,
  type ReadRow,
} from './projectTable';
import type { ScreenProps } from '../ProjectLayout';

/** Tier IS a severity ordering — strong / needs-validation / excluded — so the verdict palette fits. */
const TIER_CLASS: Record<string, string> = { A: 'v', B: 'l', C: 'x' };
const TIERS = ['A', 'B', 'C'] as const;

/** How many columns the Discovery group spans, for the absent-cells row. */
const DISCOVERY_SPAN = 4;

/**
 * The docked chat's composer, driven the way a user would.
 *
 * There is no shared store between a stage screen and the agent panel (the shell mounts them side by
 * side, not through each other), so the handoff is: find the composer already in the page and set its
 * value through the native `HTMLInputElement` setter, then dispatch a real `input` event. That is what
 * makes React's own `onChange` fire — a plain `input.value = …` changes the pixel and not the state,
 * and the next keystroke wipes it. With no composer mounted (the panel is collapsible) this is a
 * silent no-op rather than a throw, and the caller says so instead of pretending.
 */
function focusChatWithTarget(target: string): boolean {
  const input = document.querySelector<HTMLInputElement>('input[aria-label^="Message the"]');
  if (!input) return false;
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;
  if (!setter) return false;
  const prefill = `Revise ${target}: `;
  setter.call(input, prefill);
  input.dispatchEvent(new Event('input', { bubbles: true }));
  input.focus();
  input.setSelectionRange(prefill.length, prefill.length);
  return true;
}

/**
 * Discovery — the candidate pool, as the Discovery column group of the one project table.
 *
 * It is the same rows the Regulatory and Dosing screens show, widened to a different group (spec §5):
 * every record from Discovery onward is keyed on (component, CAS), so the record has always BEEN one
 * table and was merely rendered as five screens that each fetched a slice of it.
 *
 * Three things this screen must keep getting right:
 *
 *  1. **Candidates are per-component tracks.** Grouped by component, never flattened into one
 *     product-wide ranked pool — background, form, ppm and codes all run independently per component.
 *  2. **`preferred` is read off the record's own flag**, never inferred from tier or from position. A
 *     web-only candidate is capped at tier B and can never be preferred (DiscoveryAgent.Validate), so
 *     a preferred row is a claim about corpus evidence.
 *  3. **Sources are counted from the record**, and a candidate resting on none says so in the loud
 *     direction — Discovery is the stage with the heaviest provenance burden.
 *
 * No direct edits (Law 4): the operator never re-tiers a candidate by hand. The row's button only
 * hands the candidate to the agent column with its name filled in; the operator finishes the sentence.
 */
export function Discovery({ project }: ScreenProps) {
  const status = project.stages.discovery?.status;
  const { state } = useProjectTable(project.projectId, status);

  if (state.kind === 'loading') return <Loading what="the candidate pool" />;

  return (
    <>
      <section className="screen">
        {/* The pool is Discovery's INPUT, and Discovery takes minutes. Without it the operator watches
            an empty table for the whole run with no way to see what is being screened. */}
        <ProposedPool
          projectId={project.projectId}
          hint="what Discovery is corroborating against the catalog"
        />
      </section>

      <section className="screen">
        <SectionHeader
          title="Candidates"
          headingLevel={3}
          count={state.kind === 'ready' ? state.read.rows.length : undefined}
          hint="grouped by component — there is no product-wide pool"
        />

        {state.kind === 'error' && <TableError message={state.message} />}

        {state.kind === 'ready' && (
          <>
            <DroppedRows n={state.read.dropped} />

            {state.read.rows.length === 0 ? (
              <EmptyState
                icon="ti-flask-off"
                title="No candidates on the record yet."
                body={
                  <>
                    Discovery writes its pool once it has corroborated the proposed elements against
                    the catalog. Until then there is nothing to rank — this is a young project, not a
                    failure.
                  </>
                }
              />
            ) : (
              byComponentRows(state.read.rows).map(([componentId, rows]) => (
                <ComponentTable key={componentId} componentId={componentId} rows={rows} />
              ))
            )}
          </>
        )}
      </section>

      <section className="screen">
        <details>
          <summary className="small secondary" style={{ cursor: 'pointer' }}>
            Revision trail
          </summary>
          <RevisionTrail projectId={project.projectId} />
        </details>
      </section>
    </>
  );
}

function ComponentTable({ componentId, rows }: { componentId: string; rows: ReadRow[] }) {
  /*
   * Within a component, tier order is imposed and nothing else is: A before B before C, and inside a
   * tier the agent's own order is a ranking it chose, which the UI must not re-do. `.slice()` first —
   * `byComponentRows` hands back the arrays it built internally, and sorting one in place would
   * mutate its return value.
   */
  const ordered = rows.slice().sort((a, b) => tierRank(a) - tierRank(b));

  return (
    <div style={{ marginBottom: 'var(--s5)' }}>
      <SectionHeader
        eyebrow="Component"
        title={componentId}
        headingLevel={4}
        count={ordered.length}
        hint="candidates on this component's own track"
      />
      <table className="mx">
        <thead>
          <tr>
            <th>Substance</th>
            <th>Tier</th>
            <th>Preferred</th>
            <th>Rationale</th>
            <th>Sources</th>
            <th style={{ width: 40 }} />
          </tr>
        </thead>
        <tbody>
          {ordered.map((row) => (
            <tr key={`${row.componentId}|${row.cas}`}>
              <IdentityCell row={row} />
              {row.discovery.kind === 'cells' ? (
                <DiscoveryRow cells={row.discovery.cells} />
              ) : (
                <AbsentCells state={row.discovery} span={DISCOVERY_SPAN} phase="Discovery" />
              )}
              <td>
                <ReviseButton target={`${row.element} ${row.form}`} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function tierRank(row: ReadRow): number {
  const tier = row.discovery.kind === 'cells' ? row.discovery.cells.tier : undefined;
  const i = TIERS.indexOf(tier as (typeof TIERS)[number]);
  // An unrecognised or absent tier sorts LAST rather than first. It is not evidence of strength, and
  // a row whose tier this build cannot read must not be handed the top of the list by default.
  return i < 0 ? TIERS.length : i;
}

function DiscoveryRow({ cells }: { cells: DiscoveryCells }) {
  const tier = typeof cells.tier === 'string' ? cells.tier : '';
  const sources = typeof cells.sources === 'number' && Number.isFinite(cells.sources) ? cells.sources : 0;
  const rationale = typeof cells.rationale === 'string' ? cells.rationale : '';

  return (
    <>
      <td>
        {/* An unrecognised tier gets no verdict colour at all: the palette is a severity claim, and
            claiming a severity we cannot read would be worse than showing the raw token. */}
        <span className={`chip ${TIER_CLASS[tier] ?? 'chip--neutral'}`}>{tier || 'no tier'}</span>
      </td>
      <td>
        {cells.preferred === true ? (
          <span
            className="chip chip--neutral"
            title="The agent's preferred candidate on this component — unreachable for a web-only candidate"
          >
            preferred
          </span>
        ) : (
          <span className="tiny muted">—</span>
        )}
      </td>
      <td className="secondary">{rationale || <span className="muted">No rationale recorded.</span>}</td>
      {/*
        A COUNT, not chips. The projection carries how many citations the agent recorded, not the
        citations themselves — and a chip built from a count would be a citation with no source, no
        reference and no retrieval date, which is a claim rather than a citation. The real chips live
        in the evidence panel on Regulatory, where the whole `Citation` is available and can be
        rendered verbatim. Zero is the loud case: a candidate resting on nothing traces to nothing.
      */}
      <td>
        {sources === 0 ? (
          <span className="small" style={{ color: 'var(--text-warning)' }}>
            <i className="ti ti-link-off" aria-hidden="true" /> none
          </span>
        ) : (
          <span className="small">
            {sources} source{sources === 1 ? '' : 's'}
          </span>
        )}
      </td>
    </>
  );
}

/**
 * Hand this candidate to the agent column.
 *
 * The button never changes the record. Re-tiering by hand is exactly what Law 4 forbids: the operator
 * tells the agent what is wrong and why, the agent applies the change, and the reason is recorded as a
 * Learned Conclusion. A button that silently did nothing when the panel is collapsed would be a lying
 * affordance, so the draft is shown here instead.
 */
function ReviseButton({ target }: { target: string }) {
  const [draft, setDraft] = useState<string | null>(null);
  return (
    <>
      <button
        type="button"
        className="btn"
        aria-label={`Revise ${target} in chat`}
        title="Tell the agent what to change, and why"
        onClick={() => setDraft(focusChatWithTarget(target) ? null : `Revise ${target}: `)}
      >
        <i className="ti ti-message-2" aria-hidden="true" />
      </button>
      {draft && (
        <div className="tiny secondary" style={{ marginTop: 4 }}>
          The agent column is closed. Open it and start with: <span className="data">{draft}</span>
        </div>
      )}
    </>
  );
}
