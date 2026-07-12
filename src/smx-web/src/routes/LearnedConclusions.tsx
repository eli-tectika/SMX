import { useState } from 'react';
import { MockBadge } from '../components/MockBadge';
import { Meter } from '../components/ui/Meter';
import { EmptyState, SearchInput, SectionHeader } from '../components/ui/Primitives';
import conclusions from '../mocks/fixtures/learned-conclusions.json';

interface Entry {
  tag: string;
  text: string;
  scope: string;
  source: string;
  confidence: number;
}

/**
 * A tag is a CATEGORY, not a verdict. Painting `XRF` green would say "Pass" in this
 * app's colour grammar, which it does not mean. Categories are neutral and are told
 * apart by icon.
 */
const TAG_ICON: Record<string, string> = {
  XRF: 'ti-wave-square',
  material: 'ti-cube',
  regulatory: 'ti-gavel',
};

/**
 * Learned Conclusions (spec §6) — accumulated findings with provenance and confidence.
 *
 * Written whenever the operator tells an agent *why* to change something, and at
 * project close. Read at intake, discovery and dosing. Confidence is the reason an
 * entry is trustworthy or not, so it gets a meter rather than a grey percentage.
 */
export function LearnedConclusions() {
  const [q, setQ] = useState('');
  const [tag, setTag] = useState<string | null>(null);
  const { entries } = conclusions as { entries: Entry[] };

  const tags = [...new Set(entries.map((e) => e.tag))];
  const needle = q.trim().toLowerCase();
  const shown = entries
    .filter((e) => (tag ? e.tag === tag : true))
    .filter((e) =>
      needle ? `${e.tag} ${e.text} ${e.scope}`.toLowerCase().includes(needle) : true,
    );

  return (
    <section className="screen" data-provenance="mock">
      <div className="cap">
        <b>Learned conclusions</b> &nbsp;·&nbsp; spec §6 — findings with provenance and confidence
      </div>

      <MockBadge note="No conclusion here was recorded from a real agent-with-a-reason change." />

      <SearchInput
        value={q}
        onChange={setQ}
        placeholder="Search conclusions…"
        label="Search learned conclusions"
      />

      <div className="seg" role="group" aria-label="Filter by tag" style={{ marginBottom: 4 }}>
        <button className="seg__btn" onClick={() => setTag(null)} aria-pressed={tag === null}>
          all
        </button>
        {tags.map((t) => (
          <button
            key={t}
            className="seg__btn"
            onClick={() => setTag(tag === t ? null : t)}
            aria-pressed={tag === t}
          >
            <i className={`ti ${TAG_ICON[t] ?? 'ti-tag'}`} aria-hidden="true" /> {t}
          </button>
        ))}
      </div>

      <SectionHeader eyebrow="Conclusions" count={shown.length} />

      {shown.length === 0 ? (
        <EmptyState
          icon="ti-search-off"
          title="No conclusions match."
          body={q ? <>Nothing matches “{q}”.</> : undefined}
        />
      ) : (
        shown.map((e, i) => (
          <div className="card" key={i} style={{ marginBottom: 8 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
              <span className="chip chip--neutral">
                <i className={`ti ${TAG_ICON[e.tag] ?? 'ti-tag'}`} aria-hidden="true" />
                &nbsp;{e.tag}
              </span>
              <span className="tiny muted">scope: {e.scope}</span>
            </div>

            <p className="small" style={{ margin: '0 0 10px', lineHeight: 1.5 }}>
              {e.text}
            </p>

            <div style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
              <div style={{ minWidth: 190, flex: '0 1 240px' }}>
                <Meter value={e.confidence} label="confidence" />
              </div>
              <span className="src" style={{ marginLeft: 'auto' }}>
                {e.source}
              </span>
            </div>
          </div>
        ))
      )}
    </section>
  );
}
