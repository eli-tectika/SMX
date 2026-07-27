import { useState, type ReactNode } from 'react';

export interface Requirement {
  id: string;
  label: string;
  met: boolean;
  detail?: ReactNode;
  action?: { label: string; onClick: () => void };
  /**
   * Which ruling this requirement gates.
   *
   * `'both'` (the default) is for conditions the SERVER applies to every determination — the gate's own
   * armability, which the endpoint checks before it ever looks at which ruling was asked for.
   *
   * `'sign'` is for a condition the endpoint applies to APPROVALS ONLY (the VP gate's "a finalized code
   * for every component": the `rejected` branch returns before it reads dosing). Marking it keeps it out
   * of the rejection's arming — otherwise an approve-only requirement would silently make rejection
   * unreachable in a state the server accepts, and rejection is the escape hatch on the highest-
   * consequence screen in the system.
   */
  appliesTo?: 'sign' | 'both';
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
   * When provided, the reject button is LIVE — gated on the note, and on the requirements that gate a
   * REJECTION (`rejectArmed`, i.e. every requirement except the ones marked `appliesTo: 'sign'`).
   *
   * The arming half is not a UI opinion, it is the server's contract. `POST …/decision/determination`
   * runs its guards — the park check, the pending-revision check, `VpGate.Armable`, the regulatory
   * coverage re-check — BEFORE it branches on `determination`, so a rejection on an unarmed gate is
   * refused with the same 422 an approval would get. A reject button enabled over those blockers would
   * be a lying affordance, which is the precise failure `armable` is computed server-side to prevent.
   *
   * It is NOT, however, identical arming: the endpoint's `rejected` branch returns before it validates
   * the confirmations, so a requirement that only an APPROVAL must satisfy would wrongly lock the
   * rejection out of a state the server accepts. `appliesTo` is how a caller says which is which — the
   * asymmetry is read off the endpoint, never invented here, and the default stays `'both'`.
   *
   * (An earlier version of this comment argued something broader — that the VP should be able to say
   * no over ANY blocker, because refusing traps a bad decision open. That describes a system we do not
   * have. If the product ever wants a fully blocked project to be rejectable, the fix is a BACKEND
   * change to the guard order, moving the `rejected` branch ahead of the armability checks entirely.
   * The UI may not fake it.)
   *
   * The note half is unconditional: a rejection is a ruling and needs its reason exactly as much as an
   * approval does, so the note field renders whenever `onReject` is wired, whether or not the caller
   * also set `signNote`.
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
  /** Rejection arms on the requirements that gate a rejection — see `appliesTo` on Requirement. */
  const rejectArmed = requirements.every((r) => r.appliesTo === 'sign' || r.met);
  const canReject =
    Boolean(onReject) && rejectArmed && !rejectBusy && !signBusy && note.trim().length > 0;

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
                ? // `rejectArmed`, not `armed`: an approve-only requirement leaves `armed` false while
                  // this button is genuinely live, and a disabled-sounding title on a live button is
                  // the same lie as a live-looking title on a dead one.
                  !rejectArmed
                  ? 'Locked until every requirement above is met — the endpoint refuses a rejection on an unarmed gate too'
                  : note.trim().length > 0
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
          {/*
            Known dead corner: this branch keys on `!onSign` alone, so a gate wired with `onReject`
            but no `onSign` would read "this control is inert" while its reject button is genuinely
            live. No caller does that today (every live gate passes both), so it is unreachable — but
            a future reject-only gate must widen this condition rather than inherit the lie.
          */}
          {!onSign
            ? 'No endpoint to sign this gate — this control is inert.'
            : !armed
              ? // Which rulings are locked depends on WHY. The endpoint runs its armability guards
                // before it looks at the determination, so an unmet `'both'` requirement locks both —
                // but an approve-only one leaves the rejection live, and saying "no determination can
                // be recorded" over an enabled Reject button would be the lie this branch exists to
                // avoid. See `appliesTo`.
                rejectArmed && onReject
                ? 'Locked for approval until every requirement above is met — a rejection can still be recorded.'
                : 'Locked until every requirement above is met — no determination can be recorded until then.'
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
