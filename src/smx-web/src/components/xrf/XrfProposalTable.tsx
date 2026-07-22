/**
 * THIS GRID IS EDITABLE, AND THAT IS NOT AN INCONSISTENCY.
 *
 * Every other analytical surface in this app is read-only because the operator must not hand-edit
 * AGENT OUTPUT (CLAUDE.md Law 4) — a silent edit to an agent's conclusion captures no reason and
 * teaches the system nothing. This is the opposite case: these are the operator's transcription of a
 * HUMAN PHYSICIST's measurement, which no agent produced and no agent may alter (IntakeAnswers refuses
 * element pools by name). It is editable because of the same principle that makes the intake brief
 * read-only.
 *
 * Do not "fix" the inconsistency by making it read-only. That would delete the only entry point for
 * physics data for the second time — the creation form was the first.
 */
import { useEffect, useState } from 'react';
import type { XrfProposal } from '../../api/types';

/** The three verdicts the physicist reports. X is a measurement, not an omission. */
const STATUSES = ['V', 'L', 'X'] as const;

/** V and L are the pool; X is measured and rejected, and must not read as usable. */
const inPool = (status: string) => status === 'V' || status === 'L';

/** The chip family already means Pass / Conditional / Fail, which is exactly V / L / X. */
const STATUS_CHIP: Record<string, string> = { V: 'v', L: 'l', X: 'x' };

/**
 * A numeric cell that never fabricates a number.
 *
 * It keeps the operator's RAW TEXT locally and emits `null` for anything that is not a finite
 * number, because 0 is a real measurement — a genuinely zero background — and writing 0 where the
 * operator typed nonsense would invent data the physicist never reported. The text is local rather
 * than derived because a purely controlled numeric input eats the decimal point: typing "12." parses
 * to 12, re-renders as "12", and the next keystroke silently turns 12.5 into 125.
 */
function NumberCell({
  value,
  label,
  unit,
  onChange,
}: {
  value: number | null | undefined;
  label: string;
  unit?: string | null;
  onChange: (v: number | null) => void;
}) {
  const [text, setText] = useState(value == null ? '' : String(value));

  // Re-sync only when the parent's value says something this text does not already say (a fresh
  // parse replaced the row, or the row shifted up after a removal). Never mid-token.
  useEffect(() => {
    const n = Number(text);
    const settled = text.trim() === '' || !Number.isFinite(n) ? null : n;
    if ((value ?? null) !== settled) setText(value == null ? '' : String(value));
    // `text` is deliberately NOT a dependency: it is the in-flight draft, and re-running on it
    // would snap "12." back to "12" between the two halves of a decimal.
  }, [value]);

  return (
    <span style={{ display: 'inline-flex', alignItems: 'baseline', gap: 4 }}>
      <input
        type="text"
        inputMode="decimal"
        className="data"
        aria-label={label}
        value={text}
        onChange={(e) => {
          const next = e.target.value;
          setText(next);
          const n = Number(next);
          onChange(next.trim() === '' || !Number.isFinite(n) ? null : n);
        }}
      />
      {unit && <span className="tiny muted">{unit}</span>}
    </span>
  );
}

export function XrfProposalTable({
  proposals,
  components,
  onChange,
  onConfirm,
  busy,
}: {
  proposals: XrfProposal[];
  components: string[];
  onChange: (proposals: XrfProposal[]) => void;
  onConfirm: (proposals: XrfProposal[]) => void;
  busy: boolean;
}) {
  /**
   * Confirm arms only when every row is clean. This mirrors XrfConfirmation.Build and is a CONVENIENCE:
   * the server re-checks all of it and its refusal is the one that counts. Erring toward disabled is
   * deliberate — a button that arms and then fails is worse than one that explains why it will not.
   */
  const blocked = proposals.length === 0 || proposals.some((p) => p.problems.length > 0);

  const patch = (index: number, change: Partial<XrfProposal>) =>
    onChange(proposals.map((p, i) => (i === index ? { ...p, ...change } : p)));

  const drop = (index: number) => onChange(proposals.filter((_, i) => i !== index));

  return (
    <div>
      <table className="mx">
        <thead>
          <tr>
            <th style={{ width: 120 }}>Component</th>
            <th style={{ width: 80 }}>Element</th>
            <th style={{ width: 70 }}>Line</th>
            <th style={{ width: 70 }}>Status</th>
            <th style={{ width: 150 }}>Pool</th>
            <th style={{ width: 130 }}>Background</th>
            <th style={{ width: 130 }}>Device</th>
            <th style={{ width: 120 }}>LOD</th>
            <th>Signal note</th>
            <th style={{ width: 70 }} />
          </tr>
        </thead>
        <tbody>
          {proposals.map((p, i) => {
            // The row's own component may not be one the project knows about (a typo in the
            // physicist's file). Offering it keeps the select from silently rewriting the record.
            const options = components.includes(p.component)
              ? components
              : [p.component, ...components];
            return (
              <tr key={i} data-testid={`xrf-row-${p.element}`}>
                <td>
                  <select
                    aria-label={`component for ${p.element}`}
                    value={p.component}
                    onChange={(e) => patch(i, { component: e.target.value })}
                  >
                    {options.map((c) => (
                      <option key={c} value={c}>
                        {c}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <input
                    type="text"
                    className="data"
                    data-kind="element"
                    aria-label={`element for ${p.element}`}
                    value={p.element}
                    onChange={(e) => patch(i, { element: e.target.value })}
                  />
                </td>
                <td>
                  <input
                    type="text"
                    className="data"
                    aria-label={`emission line for ${p.element}`}
                    value={p.line}
                    onChange={(e) => patch(i, { line: e.target.value })}
                  />
                </td>
                <td>
                  <select
                    aria-label={`status for ${p.element}`}
                    value={p.status}
                    onChange={(e) => patch(i, { status: e.target.value })}
                  >
                    {/* Never drop a status the record actually carries just because it is not one
                        of the three we know — a select with no matching option silently rewrites
                        the physicist's result to whatever happens to be first. */}
                    {!(STATUSES as readonly string[]).includes(p.status) && (
                      <option value={p.status}>{p.status}</option>
                    )}
                    {STATUSES.map((s) => (
                      <option key={s} value={s}>
                        {s}
                      </option>
                    ))}
                  </select>
                </td>
                {/* Words, not just a chip: "X" alone reads as a usable element to anyone who has
                    not memorised the legend, and the whole point of X is that it is NOT one. */}
                <td data-testid="xrf-pool-membership">
                  <span className={`chip ${STATUS_CHIP[p.status] ?? 'chip--neutral'}`}>
                    {p.status}
                  </span>{' '}
                  <span className="tiny muted">
                    {inPool(p.status) ? 'in the pool' : 'recorded — not in the pool'}
                  </span>
                </td>
                <td>
                  <NumberCell
                    value={p.backgroundLevel}
                    unit={p.backgroundUnit}
                    label={`background level for ${p.element}`}
                    onChange={(v) => patch(i, { backgroundLevel: v })}
                  />
                </td>
                <td>
                  <input
                    type="text"
                    aria-label={`device model for ${p.element}`}
                    value={p.deviceModel ?? ''}
                    onChange={(e) => patch(i, { deviceModel: e.target.value })}
                  />
                </td>
                <td>
                  <NumberCell
                    value={p.deviceLod}
                    unit={p.deviceLodUnit}
                    label={`device LOD for ${p.element}`}
                    onChange={(v) => patch(i, { deviceLod: v })}
                  />
                </td>
                <td>
                  <input
                    type="text"
                    aria-label={`signal note for ${p.element}`}
                    value={p.signalNote ?? ''}
                    onChange={(e) => patch(i, { signalNote: e.target.value })}
                  />
                  {/* The row says WHICH cell is missing, before the server has to. */}
                  {p.problems.length > 0 && (
                    <div
                      data-testid="xrf-row-problem"
                      className="tiny"
                      style={{ color: 'var(--text-warning)', marginTop: 3 }}
                    >
                      {p.problems.join(' · ')}
                    </div>
                  )}
                </td>
                <td>
                  <button
                    type="button"
                    className="btn btn--quiet"
                    onClick={() => drop(i)}
                    aria-label={`remove ${p.element}`}
                  >
                    <i className="ti ti-trash" aria-hidden="true" />
                    Remove
                  </button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>

      <div
        className="region"
        style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}
      >
        <button
          type="button"
          className="btn primary"
          disabled={busy || blocked}
          onClick={() => onConfirm(proposals)}
        >
          Confirm the background
        </button>
        {/* Beside the button, because "what pressing this does" is part of the decision. */}
        <span className="tiny muted" data-testid="xrf-confirm-effect">
          Confirming records this measurement on the project — the element pool, the measured
          backgrounds and the device — and starts Discovery.
        </span>
      </div>
    </div>
  );
}
