import { useState, type FormEvent } from 'react';
import { ApiError, NotFound, recordLoading } from '../api/client';
import { isValidLoading } from '../domain/dosing';

/**
 * The operator un-parking Dosing (spec §1.2 — the async pause/resume loop).
 *
 * Dosing parks in `awaiting-operator` when a candidate's METAL LOADING is unknown — the mass fraction of
 * the marker element in the compound (Y₂O₃ = 0.787). It is the one number in no catalog, so the agent
 * parks rather than guessing: treating an unknown loading as 1.0 would silently under-dose by the
 * compound's whole non-metal fraction.
 *
 * Two things worth knowing about this write:
 *  - It is CROSS-PROJECT. The value is keyed by CAS alone in the knowledge layer, so answering it here
 *    answers it for every future project too. That is why `basis` is mandatory — a number this durable
 *    has to stay checkable.
 *  - It RE-RUNS the stage. The write flips dosing back to `pending` and the agent starts over; this is
 *    not an in-place edit, and the caller polls rather than patching what is on screen.
 */
export function LoadingEntryForm({
  projectId,
  onEntered,
}: {
  projectId: string;
  /** Called after a 202 — the caller restarts its poll loop to watch the re-run. */
  onEntered: () => void;
}) {
  const [cas, setCas] = useState('');
  const [element, setElement] = useState('');
  const [form, setForm] = useState('');
  const [loading, setLoading] = useState('');
  const [basis, setBasis] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const loadingNum = Number(loading);
  // Mirrors the server's rule for a fast error. The server re-checks; this is not the safety net.
  const loadingOk = loading.trim() !== '' && isValidLoading(loadingNum);
  const ready =
    cas.trim() !== '' && element.trim() !== '' && form.trim() !== '' && loadingOk && basis.trim() !== '';

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!ready || busy) return;
    setBusy(true);
    setError(null);
    try {
      const res = await recordLoading(projectId, {
        cas: cas.trim(),
        element: element.trim(),
        form: form.trim(),
        metalLoading: loadingNum,
        basis: basis.trim(),
      });
      if (res === NotFound) {
        setError('Project not found.');
        return;
      }
      onEntered();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  // `color` is explicit because every one of these inputs sits inside a `.tiny muted` label and
  // these are bare `<input>`s — no `type` attribute, so base.css's `input[type='text']` rule never
  // matches them and nothing was setting their ink. What the operator had typed was inheriting
  // muted grey from the label naming it.
  const field = {
    width: '100%',
    font: 'inherit',
    color: 'var(--text-primary)',
    fontSize: 'var(--t-small)',
    padding: '6px 8px',
    border: '0.5px solid var(--border-strong)',
    borderRadius: 'var(--r1)',
  } as const;

  return (
    <form onSubmit={submit} className="region" style={{ marginBottom: 'var(--s4)' }}>
      {/* The form's heading. It was a raw `13`, which is not a token and was also lighter and
          smaller than the paragraph it heads once that paragraph became prose. --t-lead, semibold,
          and a hairline the group did not have — `.region`'s own border frames the form, it does
          not separate the title from the fields. */}
      <div
        style={{
          fontSize: 'var(--t-lead)',
          fontWeight: 'var(--w-semibold)',
          borderBottom: 'var(--hair) solid var(--border)',
          paddingBottom: 'var(--s1)',
          marginBottom: 'var(--s2)',
        }}
      >
        Enter the metal loading
      </div>
      {/* READ — this is the explanation of a number the operator is being asked to source and
          which will then be believed by every future project. It is the last thing that should
          have been set in muted grey at the floor. */}
      <p className="prose" style={{ margin: '0 0 var(--s3)' }}>
        The mass fraction of the marker element in the compound — Y<sub>2</sub>O<sub>3</sub> is 0.787.
        Dosing parked rather than guess it. This is stored against the CAS across every project, so it is
        asked once.
      </p>

      {/* REFERENCED, every label in this grid — "CAS", "Element", "Form", "Metal loading (0–1]"
          are identified at a glance, not parsed, so they stay at --t-small and stay muted. That is
          also what keeps the grid honest: the columns are `minmax(140px, 1fr)`, and the longest
          label needs ~112px at --t-small but ~131px at --t-read, which would leave 9px of slack in
          a 140px track before any padding. The judgement and the layout agree here; where they had
          not, the layout would have had to change, not the judgement. */}
      <div
        style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))', gap: 'var(--s2)' }}
      >
        <label className="tiny muted">
          CAS
          <input
            className="data"
            value={cas}
            onChange={(e) => setCas(e.target.value)}
            placeholder="1314-36-9"
            aria-label="CAS"
            style={field}
          />
        </label>
        <label className="tiny muted">
          Element
          <input
            className="data"
            value={element}
            onChange={(e) => setElement(e.target.value)}
            placeholder="Y"
            aria-label="Element"
            style={field}
          />
        </label>
        <label className="tiny muted">
          Form
          <input
            value={form}
            onChange={(e) => setForm(e.target.value)}
            placeholder="oxide"
            aria-label="Form"
            style={field}
          />
        </label>
        <label className="tiny muted">
          Metal loading (0–1]
          <input
            className="data"
            value={loading}
            onChange={(e) => setLoading(e.target.value)}
            placeholder="0.787"
            inputMode="decimal"
            aria-label="Metal loading"
            style={{
              ...field,
              borderColor: loading.trim() !== '' && !loadingOk ? 'var(--text-danger)' : field.border,
            }}
          />
        </label>
      </div>

      {/* READ — a correction, and one whose whole job is to be understood rather than noticed. */}
      {loading.trim() !== '' && !loadingOk && (
        <div className="prose" style={{ color: 'var(--text-danger)', marginTop: 4 }}>
          A metal loading is a mass fraction in (0, 1] — 0.787, not 78.7.
        </div>
      )}

      {/* The LABEL is referenced; what gets typed under it is not. The basis is the operator's own
          sentence justifying a number that every future project will then believe — the same kind
          of writing as a revision reason, and it is set to match at --t-read. */}
      <label className="tiny muted" style={{ display: 'block', marginTop: 'var(--s2)' }}>
        Basis — required
        <textarea
          value={basis}
          onChange={(e) => setBasis(e.target.value)}
          placeholder="The source that makes this number checkable — e.g. 'Sigma-Aldrich CoA, lot #…' or 'stoichiometric Y2O3'."
          rows={2}
          aria-label="Basis"
          style={{
            ...field,
            fontSize: 'var(--t-read)',
            lineHeight: 'var(--lh-prose)',
            resize: 'vertical',
          }}
        />
      </label>

      <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--s2)', marginTop: 'var(--s2)' }}>
        <button type="submit" className="btn primary" disabled={!ready || busy}>
          <i className={`ti ${busy ? 'ti-loader' : 'ti-player-play'}`} aria-hidden="true" /> Record it and
          re-run dosing
        </button>
        {/* READ — it states the consequence of the press beside it: this is not an edit, the
            stage re-runs. A helper sentence is helper text; it is still a sentence. */}
        <span className="prose">The agent starts over with this value.</span>
      </div>

      {/* READ — why the write did not land. */}
      {error && (
        <div className="prose" style={{ color: 'var(--text-danger)', marginTop: 'var(--s2)' }}>
          <i className="ti ti-alert-triangle" aria-hidden="true" /> {error}
        </div>
      )}
    </form>
  );
}
