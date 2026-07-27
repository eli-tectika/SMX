import type { ComponentDecision, DosingDoc, MsdsEntry } from '../api/types';
import { Data } from './ui/Data';
import { SectionHeader } from './ui/Primitives';

/**
 * Procurement — the Decision screen's post-close half, visible only once the record says `released`.
 *
 * The orderable set is the markers of CONFIRMED codes, never the decision rows and never a proposal:
 * "you cannot order what the VP did not sign". Each order is independently gated on a REVIEWED MSDS
 * (§5), and the button is disabled with the reason rather than hidden — a missing safety sheet is
 * what blocks an order, and hiding the control would hide the blocker with it.
 *
 * `sheetsUnknown` is the third state, and the reason this component takes a flag rather than just an
 * empty list: the MSDS registry is a CROSS-PROJECT read that can fail on its own (see `Decision.load`).
 * "We could not check" is not "there is no sheet", and printing the second when the first is true is a
 * fabricated claim about an absence — on the control that actually places the order. So an unknown
 * sheet says so, and the order is WITHHELD. Note this is the opposite of `Cost.tsx`, which treats an
 * unknown sheet as orderable: Cost only *describes* the rule, this button *executes* it, and the
 * safe default flips with the consequence.
 */
export function Procurement({
  components,
  dosing,
  sheets,
  sheetsUnknown,
  ordered,
  ordering,
  error,
  onOrder,
}: {
  components: ComponentDecision[];
  dosing: DosingDoc | null;
  sheets: MsdsEntry[];
  /** The MSDS registry read failed — say nothing about any substance's sheet, and order nothing. */
  sheetsUnknown: boolean;
  ordered: string[];
  ordering: string | null;
  error: string | null;
  onOrder: (cas: string) => void;
}) {
  const markers = components
    .filter((c) => c.confirmedCode !== null)
    .flatMap((c) =>
      (dosing?.codes ?? [])
        .filter((k) => k.componentId === c.componentId && k.ratioSignature === c.confirmedCode)
        .flatMap((k) => k.markers.map((m) => ({ ...m, componentId: c.componentId }))),
    );

  return (
    <>
      <SectionHeader
        eyebrow="Procurement"
        count={markers.length}
        hint="the markers of the signed codes — each order gated on a reviewed MSDS"
      />

      {error && (
        <div className="banner warn" role="alert" style={{ marginBottom: 8 }}>
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The order was refused.</b>
            <div className="tiny" style={{ marginTop: 3 }}>{error}</div>
          </div>
        </div>
      )}

      {sheetsUnknown && (
        <div className="banner warn" role="alert" style={{ marginBottom: 8 }}>
          <i className="ti ti-file-alert" aria-hidden="true" />
          <div>
            <b>The MSDS registry did not load.</b>
            <div className="tiny" style={{ marginTop: 3 }}>
              Every safety-sheet status below is <b>unknown</b> — not cleared, and not missing. Spec §5
              makes a reviewed sheet a hard precondition for an order, so ordering is withheld until the
              registry can be read. Reload, or open the registry directly.
            </div>
          </div>
        </div>
      )}

      <table className="mx">
        <caption className="sr-only">
          The markers of the signed codes, one row per substance, with its MSDS review status and an
          order control that stays disabled until a reviewed safety sheet is on file.
        </caption>
        <thead>
          <tr>
            <th>Substance</th>
            <th>Component</th>
            <th>MSDS</th>
            <th style={{ width: 90 }} />
          </tr>
        </thead>
        <tbody>
          {markers.map((m) => {
            const sheet = sheetsUnknown ? undefined : sheets.find((s) => s.cas === m.cas);
            const reviewed = !sheetsUnknown && sheet?.reviewStatus === 'reviewed';
            const isOrdered = ordered.includes(m.cas);
            return (
              <tr key={`${m.componentId}|${m.cas}`}>
                <td>
                  <span style={{ fontWeight: 500 }}>{m.element}</span>{' '}
                  <span className="tiny muted">
                    <Data kind="code">{m.cas}</Data>
                  </span>
                </td>
                <td className="tiny">{m.componentId}</td>
                <td className="tiny">
                  {sheetsUnknown ? (
                    <span style={{ color: 'var(--text-warning)' }}>
                      unknown — the registry did not load
                    </span>
                  ) : reviewed ? (
                    <span style={{ color: 'var(--text-success)' }}>reviewed</span>
                  ) : (
                    <span style={{ color: 'var(--text-danger)' }}>
                      {sheet ? sheet.reviewStatus : 'no sheet on file'}
                    </span>
                  )}
                </td>
                <td>
                  {isOrdered ? (
                    <span className="chip chip--neutral">ordered</span>
                  ) : (
                    <button
                      className="btn"
                      disabled={!reviewed || ordering === m.cas}
                      onClick={() => onOrder(m.cas)}
                      title={
                        sheetsUnknown
                          ? 'The MSDS registry did not load — MSDS-before-order cannot be verified, so this order is withheld'
                          : reviewed
                            ? undefined
                            : 'MSDS-before-order: a reviewed safety sheet is required before this can be ordered'
                      }
                    >
                      Order
                    </button>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </>
  );
}
