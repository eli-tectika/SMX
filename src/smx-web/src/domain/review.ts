/**
 * The review ledger — which matrix cells the operator has opened IN THIS BROWSER.
 *
 * Spec §1.8: "Gates will not arm until the agent's flagged/low-confidence items
 * have been opened."
 *
 * The backend is AUTHORITATIVE about this, and this ledger is not. `MatrixCell.evidenceReviewed`
 * ships server truth (written by POST /regulatory/reviewed and by recording a determination), and it
 * is what `RegulatoryGate.Armable` actually reads. The EvidencePanel shows that server field on every
 * cell, labelled as the one the gate reads.
 *
 * This ledger survives beside it because the two answer different questions: the server knows what was
 * REVIEWED (a recorded act), while this knows what this operator has OPENED in this session — and this
 * screen never writes to the server, so without it the matrix could not show the operator their own
 * progress through the flagged cells at all.
 *
 * Three properties make it safe:
 *
 *  1. It is a genuine record of what THIS operator opened in THIS browser, and is
 *     labelled as such on every surface that reads it. It is never presented as
 *     part of the signed record.
 *  2. It can only ever WITHHOLD arming, never grant it. Nothing in the UI signs a
 *     gate: the sign controls are hard-disabled and no screen is wired to the
 *     determination endpoint, so this ledger can never grant anything.
 *  3. An entry is written only when the operator actually expands the evidence for
 *     that cell. Nothing marks itself reviewed.
 *
 * It therefore makes rubber-stamping harder, which is the whole point.
 */

const key = (projectId: string) => `smx.reviewed.${projectId}`;

export function readReviewed(projectId: string): Set<string> {
  try {
    const raw = localStorage.getItem(key(projectId));
    if (!raw) return new Set();
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? new Set(parsed.filter((x): x is string => typeof x === 'string')) : new Set();
  } catch {
    return new Set();
  }
}

export function markReviewed(projectId: string, cellKey: string): Set<string> {
  const next = readReviewed(projectId);
  next.add(cellKey);
  try {
    localStorage.setItem(key(projectId), JSON.stringify([...next]));
  } catch {
    /* private mode / quota — the ledger degrades to "nothing opened", which withholds. */
  }
  return next;
}

/** How many of the flagged cells have been opened. Never rounds up. */
export function reviewProgress(
  flagged: readonly string[],
  reviewed: ReadonlySet<string>,
): { opened: number; total: number; remaining: string[] } {
  const remaining = flagged.filter((k) => !reviewed.has(k));
  return { opened: flagged.length - remaining.length, total: flagged.length, remaining };
}
