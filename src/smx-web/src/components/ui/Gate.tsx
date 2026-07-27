import { useState, type ReactNode } from 'react';

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
 * `kind` is a real distinction, not styling: a HARD gate blocks (regulatory, VP), a SOFT one advises
 * (dosing's code-finalization checkpoint, which records that a review happened and unlocks nothing).
 * The copy a caller passes must not blur them.
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
  onSign,
  signBusy,
  signNote,
  onReject,
  rejectBusy,
}: {
  kind: 'hard' | 'soft';
  title: string;
  records: string;
  requirements: Requirement[];
  signLabel: string;
  rejectLabel?: string;
  ledgerNote?: boolean;
  /**
   * When provided, the sign button is LIVE: enabled only at full arming, and it calls this.
   * Omitted (the gates with no endpoint — VP), the button stays disabled, because a live-looking
   * button would promise what the system cannot do.
   *
   * The argument carries `signNote`'s text when that prop is set, and is undefined otherwise — so a
   * caller that does not ask for a note can ignore it entirely.
   */
  onSign?: (note?: string) => void;
  signBusy?: boolean;
  /**
   * Ask for a mandatory note as part of signing. Dosing's soft checkpoint needs one — the note IS the
   * record of whichever ruling is made, and the backend 422s a blank one. Signing stays disabled until
   * it is non-blank, so the note cannot be skipped by clicking fast. Rejection always needs a reason
   * regardless of this prop — see `onReject`.
   */
  signNote?: { placeholder: string };
  /**
   * When provided, the reject button is LIVE. Deliberately NOT gated on `armed`: a gate that will not
   * let the VP say no until every blocker clears traps a bad decision open. It IS gated on the note —
   * a rejection is a ruling and needs its reason exactly as much as an approval does — so the note
   * field renders whenever `onReject` is wired, whether or not the caller also set `signNote`.
   */
  onReject?: (note: string) => void;
  rejectBusy?: boolean;
}) {
  const [note, setNote] = useState('');
  const met = requirements.filter((r) => r.met).length;
  const total = requirements.length;
  const armed = met === total;
  const noteReady = !signNote || note.trim().length > 0;
  const canSign = Boolean(onSign) && armed && !signBusy && noteReady;
  const canReject = Boolean(onReject) && !rejectBusy && !signBusy && note.trim().length > 0;

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

      {/* The note is the record of whichever ruling is made — approval or rejection — not an afterthought beside it. */}
      {(onReject || (signNote && onSign)) && (
        <textarea
          value={note}
          onChange={(e) => setNote(e.target.value)}
          placeholder={signNote?.placeholder ?? 'The reason for this determination'}
          rows={2}
          aria-label={`${title} — note`}
          disabled={signBusy || rejectBusy}
          style={{
            width: '100%',
            font: 'inherit',
            fontSize: 'var(--t-small)',
            padding: '6px 8px',
            marginTop: 'var(--s2)',
            border: '0.5px solid var(--border-strong)',
            borderRadius: 'var(--r1)',
            resize: 'vertical',
          }}
        />
      )}

      <div className="gatebox__actions">
        <button
          className="btn primary"
          disabled={!canSign}
          onClick={() => onSign?.(signNote ? note.trim() : undefined)}
          title={
            onSign
              ? armed
                ? noteReady
                  ? undefined
                  : 'A note is required — it records what was reviewed'
                : 'Locked until every requirement above is met'
              : 'No endpoint exists to sign this gate'
          }
        >
          <i className={`ti ${signBusy ? 'ti-loader' : 'ti-signature'}`} aria-hidden="true" />{' '}
          {signLabel}
        </button>
        {rejectLabel && (
          <button
            className="btn"
            disabled={!canReject}
            onClick={() => onReject?.(note.trim())}
            title={
              onReject
                ? note.trim().length > 0
                  ? undefined
                  : 'A reason is required — a rejection is a ruling, not a dismissal'
                : 'Disabled — no gate endpoint'
            }
          >
            <i className={`ti ${rejectBusy ? 'ti-loader' : 'ti-ban'}`} aria-hidden="true" />{' '}
            {rejectLabel}
          </button>
        )}
        <span className="tiny" style={{ color: 'var(--text-warning)', alignSelf: 'center' }}>
          {!onSign
            ? 'No endpoint to sign this gate — this control is inert.'
            : !armed
              ? canReject
                ? 'Cannot be approved yet — every requirement above must be met. It can be rejected with the reason given.'
                : 'Locked until every requirement above is met.'
              : !noteReady
                ? 'A note is required — it records what was reviewed.'
                : kind === 'soft'
                  ? 'Records that the review happened. It does not unlock anything.'
                  : 'Requirements met — sign to record the determination.'}
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
