import type { ReactNode } from 'react';

export interface Requirement {
  id: string;
  label: string;
  met: boolean;
  detail?: ReactNode;
  action?: { label: string; onClick: () => void };
}

/**
 * A gate, as the subject of its screen rather than a banner at the bottom of it.
 *
 * Spec §1.8: "Gates will not arm until the agent's flagged/low-confidence items
 * have been opened." So the gate does not merely announce that it is locked — it
 * enumerates exactly which requirements are unmet and links to each one. Making
 * the remaining work concrete and reachable is what makes rubber-stamping hard.
 *
 * The sign control is disabled REGARDLESS of arming. A gate is an operator-signed
 * record and no endpoint exists to sign one; a button that looked live at 4-of-4
 * would be promising something the system cannot do.
 *
 * The arming meter never animates on update, and nothing here ever sweeps to
 * "unlocked". Drama belongs to withholding, never to granting.
 */
export function Gate({
  kind,
  title,
  records,
  requirements,
  signLabel,
  rejectLabel,
  ledgerNote,
}: {
  kind: 'hard' | 'soft';
  title: string;
  records: string;
  requirements: Requirement[];
  signLabel: string;
  rejectLabel?: string;
  ledgerNote?: boolean;
}) {
  const met = requirements.filter((r) => r.met).length;
  const total = requirements.length;
  const armed = met === total;

  return (
    <section className="gatebox" data-kind={kind} aria-label={title}>
      <div className="gatebox__head">
        <i
          className={`ti ${kind === 'hard' ? 'ti-lock' : 'ti-eye-exclamation'}`}
          aria-hidden="true"
          style={{ color: 'var(--text-warning)' }}
        />
        <span className="gatebox__title">{title}</span>
        <span className="gatebox__sub">
          {kind === 'hard' ? 'hard lock' : 'soft review'} · {records}
        </span>
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
        <span className="tiny" style={{ color: 'var(--text-warning)', minWidth: 46 }}>
          Arming
        </span>
        <div
          className="meter"
          style={{ flex: 1, background: 'color-mix(in srgb, var(--text-warning) 15%, transparent)' }}
          role="meter"
          aria-valuenow={met}
          aria-valuemin={0}
          aria-valuemax={total}
          aria-label="Gate arming progress"
        >
          <div
            className="meter__fill"
            style={{ width: `${total ? (met / total) * 100 : 0}%`, background: 'var(--text-warning)' }}
          />
        </div>
        <span
          className="meter__num"
          style={{ color: 'var(--text-warning)', minWidth: 76, fontVariantNumeric: 'tabular-nums' }}
        >
          {met} of {total} met
        </span>
      </div>

      <div>
        {requirements.map((r) => (
          <div className="gatebox__req" key={r.id} data-met={r.met}>
            <i
              className={`ti ${r.met ? 'ti-check' : 'ti-x'}`}
              aria-hidden="true"
              style={{ marginTop: 2 }}
            />
            <div style={{ flex: 1 }}>
              <span>{r.label}</span>
              {r.detail && <div className="gatebox__req-detail">{r.detail}</div>}
            </div>
            {r.action && !r.met && (
              <button className="btn" onClick={r.action.onClick} style={{ flex: 'none' }}>
                {r.action.label}
              </button>
            )}
          </div>
        ))}
      </div>

      <div className="gatebox__actions">
        <button
          className="btn"
          disabled
          title="Disabled — a gate is an operator-signed record and no endpoint exists to sign one"
        >
          <i className="ti ti-signature" aria-hidden="true" /> {signLabel}
        </button>
        {rejectLabel && (
          <button className="btn" disabled title="Disabled — no gate endpoint">
            {rejectLabel}
          </button>
        )}
        <span className="tiny" style={{ color: 'var(--text-warning)', alignSelf: 'center' }}>
          {armed
            ? 'Requirements met — but signing has no endpoint, so this stays disabled.'
            : 'Locked until every requirement above is met.'}
        </span>
      </div>

      {ledgerNote && (
        <div className="gatebox__ledger-note">
          <i className="ti ti-device-floppy" aria-hidden="true" /> Review ledger — local to this
          browser, not part of the signed record. It can only withhold arming, never grant it.
        </div>
      )}
    </section>
  );
}
