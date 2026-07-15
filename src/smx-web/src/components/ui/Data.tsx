import type { ReactNode } from 'react';

/**
 * A machine-readable value.
 *
 * The typographic law of this app is: **every value a machine produced or a spec defines
 * is monospaced; every word a human wrote is not.** CAS numbers, ppm, element symbols,
 * emission lines, marker codes, H-codes, SML limits, prices, lead times, dates, project
 * ids — all of them are `Data`. Prose, labels, and headings are not.
 *
 * That rule does more for the scientific-instrument character of the interface than the
 * choice of typeface did. It is also the rule most likely to rot: applied by hand it
 * lands on whatever the author remembered (before this component existed, mono reached
 * CAS numbers and project ids and nothing else — ppm, prices and dates all rendered in
 * sans, in the middle of a chemistry tool). A primitive is what makes it total.
 *
 * `kind` is not decoration and does not currently change much: it is a hook for the
 * per-kind treatments the domain will eventually want (a slashed zero in a CAS number,
 * decimal alignment in a ppm column) and, more immediately, it makes the sweep auditable
 * — you can grep for what is and is not tagged.
 */
export type DataKind =
  | 'cas'
  | 'ppm'
  | 'code'
  | 'date'
  | 'price'
  | 'id'
  | 'element'
  | 'line'
  | 'hcode'
  | 'num';

export function Data({
  kind,
  children,
  title,
}: {
  kind?: DataKind;
  children: ReactNode;
  title?: string;
}) {
  return (
    <span className="data" data-kind={kind} title={title}>
      {children}
    </span>
  );
}
