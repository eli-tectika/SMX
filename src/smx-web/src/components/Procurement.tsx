import type { ComponentDecision, DosingDoc, MsdsEntry } from '../api/types';
import { Data } from './ui/Data';

/**
 * How much this screen actually knows about the MSDS registry.
 *
 * A boolean could not express it, and the missing third state was a real fabrication: `Decision.load`
 * reaches `phase = 'ready'` on the three project reads and only THEN awaits the registry, so for the
 * whole of that cross-project round trip the sheet list is empty and no failure has occurred. A
 * two-state flag rendered that window as `no sheet on file` for every substance — the exact sentence
 * `Cost.tsx` calls a fabricated claim about an absence, printed on the screen that places the order.
 *
 * `'unread'` and `'failed'` both withhold the order; only they differ in what they SAY, and only
 * `'failed'` is an incident worth a banner.
 */
export type SheetsState = 'unread' | 'ok' | 'failed';

/**
 * Procurement — the Decision screen's post-close half, visible only once the record says `released`.
 *
 * The orderable set is the markers of CONFIRMED codes, never the decision rows and never a proposal:
 * "you cannot order what the VP did not sign". Each order is independently gated on a REVIEWED MSDS,
 * and the button is disabled with the reason rather than hidden — a missing safety sheet is what
 * blocks an order, and hiding the control would hide the blocker with it. The precondition is stated
 * in words HERE, where the order is placed, and not only on a registry page nobody is looking at.
 *
 * `sheetsState` is why this takes a state rather than just a list: an empty list means "no sheets" and
 * nothing else, and the two ways of not knowing — not read yet, could not be read — are not that. Only
 * a sheet we actually read may be described. Note the polarity is the OPPOSITE of `Cost.tsx`, which
 * treats an unknown sheet as orderable: Cost only *describes* MSDS-before-order, this button
 * *executes* it, and the safe default flips with the consequence.
 */
export function Procurement({
  components,
  dosing,
  sheets,
  sheetsState,
  ordered,
  ordering,
  error,
  onOrder,
}: {
  components: ComponentDecision[];
  dosing: DosingDoc | null;
  sheets: MsdsEntry[];
  /** Whether `sheets` may be believed at all — see `SheetsState`. */
  sheetsState: SheetsState;
  ordered: string[];
  ordering: string | null;
  error: string | null;
  onOrder: (cas: string) => void;
}) {
  const known = sheetsState === 'ok';
  // `client.ts` casts every response with `as` and validates nothing, so neither array is assumed:
  // a drifted DosingDoc must not turn the order table into a TypeError on the screen that buys.
  const codes = Array.isArray(dosing?.codes) ? dosing!.codes : [];
  const sheetList = Array.isArray(sheets) ? sheets : [];
  const markers = components
    .filter((c) => c.confirmedCode !== null)
    .flatMap((c) =>
      codes
        .filter((k) => k.componentId === c.componentId && k.ratioSignature === c.confirmedCode)
        .flatMap((k) =>
          (Array.isArray(k.markers) ? k.markers : []).map((m) => ({
            ...m,
            componentId: c.componentId,
          })),
        ),
    );

  return (
    <>
      <p className="prose" style={{ margin: '0 0 var(--s3)' }}>
        <b>MSDS before order.</b> A substance cannot be ordered until a reviewed safety sheet is on
        file for it — the button below stays dead until one is, and the row says why. These{' '}
        {markers.length} substance{markers.length === 1 ? '' : 's'}{' '}
        {markers.length === 1 ? 'is' : 'are'} the markers of the codes the VP signed; nothing else on
        this project is orderable.
      </p>

      {error && (
        <div className="banner warn" role="alert" style={{ marginBottom: 8 }}>
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The order was refused.</b>
            <div className="tiny" style={{ marginTop: 3 }}>{error}</div>
          </div>
        </div>
      )}

      {/* Scoped to 'failed' alone. A banner during 'unread' would announce an incident that has not
          happened — the loading state's own false claim, in the opposite direction. */}
      {sheetsState === 'failed' && (
        <div className="banner warn" role="alert" style={{ marginBottom: 8 }}>
          <i className="ti ti-file-alert" aria-hidden="true" />
          <div>
            <b>The MSDS registry did not load.</b>
            <div className="tiny" style={{ marginTop: 3 }}>
              Every safety-sheet status below is <b>unknown</b> — not cleared, and not missing. A
              reviewed sheet is a hard precondition for an order, so ordering is withheld until the
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
            const sheet = known ? sheetList.find((s) => s.cas === m.cas) : undefined;
            const reviewed = known && sheet?.reviewStatus === 'reviewed';
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
                  {sheetsState === 'unread' ? (
                    <span className="muted">checking…</span>
                  ) : sheetsState === 'failed' ? (
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
                        sheetsState === 'unread'
                          ? 'The MSDS registry has not come back yet — MSDS-before-order cannot be verified until it does'
                          : sheetsState === 'failed'
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
