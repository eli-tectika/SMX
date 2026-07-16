import { useState } from 'react';
import { getLearnedConclusions } from '../api/client';
import type { ConclusionScope, LearnedConclusion } from '../api/types';
import { Data } from '../components/ui/Data';
import { Meter } from '../components/ui/Meter';
import { EmptyState, SearchInput, SectionHeader } from '../components/ui/Primitives';
import { useKnowledge } from '../hooks/useKnowledge';

/**
 * A kind is a CATEGORY, not a verdict. Painting `xrf-background` green would say "Pass" in
 * this app's colour grammar, which it does not mean. Categories stay neutral and are told
 * apart by icon.
 */
const KIND_ICON: Record<string, string> = {
  xrf: 'ti-wave-square',
  'xrf-background': 'ti-wave-square',
  material: 'ti-cube',
  regulatory: 'ti-gavel',
  'regulatory-judgment': 'ti-gavel',
};

function iconFor(kind: string): string {
  return KIND_ICON[kind?.toLowerCase()] ?? 'ti-bulb';
}

/**
 * Learned Conclusions (spec §6) — accumulated findings, with provenance and confidence.
 *
 * Previously a fixture behind a MockBadge. `GET /learned-conclusions?search=` exists and is
 * served from Cosmos, so the badge is gone and every finding here is a real record.
 *
 * This is "the mechanism by which the system gets smarter" (spec §6): a conclusion is written
 * whenever the operator tells an agent *why* to change something, and again at project close.
 * Which means the honest empty state is important — a new system has learned nothing, and
 * saying so is the only way the operator can tell the difference between "we have no prior
 * knowledge here" and "the knowledge layer is broken".
 *
 * Confidence gets a meter rather than a grey percentage, because confidence is the whole
 * reason a finding is or is not safe to lean on. The meter is neutral, never green: a
 * high-confidence finding is not a Pass.
 */
export function LearnedConclusions() {
  const [q, setQ] = useState('');
  const [kind, setKind] = useState<string | null>(null);

  const state = useKnowledge<LearnedConclusion>(getLearnedConclusions, q);

  if (state.kind === 'loading') {
    return (
      <section className="screen">
        <Head />
        <p className="muted small">Loading conclusions…</p>
      </section>
    );
  }

  if (state.kind === 'error') {
    return (
      <section className="screen">
        <Head />
        <div className="banner danger">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>Could not read the learned conclusions.</b>
            <div style={{ marginTop: 3 }}>{state.message}</div>
          </div>
        </div>
      </section>
    );
  }

  const entries = state.items;
  const kinds = [...new Set(entries.map((e) => e.kind))];
  const shown = entries.filter((e) => (kind ? e.kind === kind : true));

  return (
    <section className="screen">
      <Head />

      <SearchInput
        value={q}
        onChange={setQ}
        placeholder="Search findings, elements, materials, markets…"
        label="Search the learned conclusions"
      />

      {kinds.length > 0 && (
        <div style={{ display: 'flex', gap: 6, margin: '10px 0 0', flexWrap: 'wrap' }}>
          <button
            className="qr"
            onClick={() => setKind(null)}
            aria-pressed={kind === null}
            style={kind === null ? { borderColor: 'var(--text-accent)', color: 'var(--text-accent)' } : undefined}
          >
            All
          </button>
          {kinds.map((k) => (
            <button
              key={k}
              className="qr"
              onClick={() => setKind(kind === k ? null : k)}
              aria-pressed={kind === k}
              style={kind === k ? { borderColor: 'var(--text-accent)', color: 'var(--text-accent)' } : undefined}
            >
              <i className={`ti ${iconFor(k)}`} aria-hidden="true" /> {k}
            </button>
          ))}
        </div>
      )}

      <SectionHeader eyebrow="Conclusions" count={shown.length} />

      {shown.length === 0 ? (
        <EmptyState
          icon="ti-bulb-off"
          title={q || kind ? 'Nothing matches.' : 'Nothing has been learned yet.'}
          body={
            q || kind ? (
              <>No conclusion matches that filter.</>
            ) : (
              <>
                A conclusion is written when you tell an agent <i>why</i> to change something,
                and again when a project closes. Until then the system has no prior knowledge to
                bring to a new project — which is a fact worth seeing, not a gap to fill.
              </>
            )
          }
        />
      ) : (
        <div className="card-list">
          {shown.map((e) => (
            <article className="card" key={e.id}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 6 }}>
                <span className="chip chip--neutral">
                  <i className={`ti ${iconFor(e.kind)}`} aria-hidden="true" />
                  &nbsp;{e.kind}
                </span>
                <span className="tiny muted">{scopeText(e.scope)}</span>
                <span className="tiny muted" style={{ marginLeft: 'auto' }}>
                  <Data kind="date">{e.createdAt.slice(0, 10)}</Data>
                </span>
              </div>

              <p className="prose" style={{ margin: '0 0 8px' }}>
                {e.finding}
              </p>

              <div style={{ maxWidth: 260, marginBottom: 6 }}>
                <div className="tiny muted" style={{ marginBottom: 3 }}>
                  Confidence
                </div>
                <Meter value={e.confidence} />
              </div>

              <div className="tiny muted">
                {e.provenance.sourceProjects.length > 0 ? (
                  <>
                    from{' '}
                    {e.provenance.sourceProjects.map((p) => (
                      <span className="src data" key={p}>
                        {p}
                      </span>
                    ))}
                  </>
                ) : (
                  <span>no source project recorded</span>
                )}
                {e.supersedes && (
                  <span> · refines <Data kind="id">{e.supersedes}</Data></span>
                )}
              </div>
            </article>
          ))}
        </div>
      )}
    </section>
  );
}

function Head() {
  return (
    <div className="cap">
      <b>Learned conclusions</b>
      Findings with provenance and confidence
    </div>
  );
}

/** The scope is what a finding is *about*, and it is what makes the finding reusable. */
function scopeText(s: ConclusionScope): string {
  return (
    [s.element, s.form, s.substance, s.material, s.application, s.market]
      .filter(Boolean)
      .join(' · ') || 'general'
  );
}
