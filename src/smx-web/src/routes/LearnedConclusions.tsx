import { useState } from 'react';
import { MockBadge } from '../components/MockBadge';
import conclusions from '../mocks/fixtures/learned-conclusions.json';

interface Entry {
  tag: string;
  text: string;
  scope: string;
  source: string;
  confidence: number;
}

const TAG_CLASS: Record<string, string> = { XRF: 'v', material: 'l', regulatory: 'n' };

/**
 * Learned Conclusions (spec §6) — accumulated findings with provenance and confidence.
 *
 * Written whenever the operator tells an agent *why* to change something, and at
 * project close. Read at intake, discovery and dosing.
 */
export function LearnedConclusions() {
  const [q, setQ] = useState('');
  const { entries } = conclusions as { entries: Entry[] };
  const needle = q.trim().toLowerCase();
  const shown = needle
    ? entries.filter((e) => `${e.tag} ${e.text} ${e.scope}`.toLowerCase().includes(needle))
    : entries;

  return (
    <section className="screen">
      <div className="cap">
        <b>Learned conclusions</b> &nbsp;·&nbsp; spec §6 — findings with provenance and confidence
      </div>

      <MockBadge note="No conclusion here was recorded from a real agent-with-a-reason change." />

      <input
        type="text"
        value={q}
        onChange={(e) => setQ(e.target.value)}
        placeholder="Search conclusions…"
        aria-label="Search learned conclusions"
        style={{ marginBottom: 14 }}
      />

      {shown.map((e, i) => (
        <div className="region" key={i} style={{ marginBottom: 10 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 5 }}>
            <span className={`chip ${TAG_CLASS[e.tag] ?? 'l'}`}>{e.tag}</span>
            <span className="tiny muted">scope: {e.scope}</span>
            <span className="tiny muted" style={{ marginLeft: 'auto' }}>
              confidence {(e.confidence * 100).toFixed(0)}%
            </span>
          </div>
          <p className="small" style={{ margin: '0 0 5px' }}>
            {e.text}
          </p>
          <span className="src">{e.source}</span>
        </div>
      ))}
      {shown.length === 0 && (
        <p className="small muted" style={{ textAlign: 'center', padding: 20 }}>
          No conclusions match “{q}”.
        </p>
      )}
    </section>
  );
}
