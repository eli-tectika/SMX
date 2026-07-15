import type { VerdictStatus } from '../api/types';
import { VERDICT_SEVERITY } from '../api/types';

/**
 * A regulatory-gate blocker, parsed from the string the backend emits.
 *
 * RegulatoryGate.Armable formats a per-cell blocker as exactly
 *   "unreviewed: {cas}|{componentId} ({Overall})"
 * and the GET endpoint may additionally prepend a non-cell blocker
 *   "incomplete: not every candidate has a verdict yet"
 *
 * We parse the cell blockers into something the UI can act on (a link that opens the offending cell)
 * and treat anything else as a plain message. Displaying the raw string would leak the wire format and,
 * worse, offer no way to reach the cell it names.
 */
export interface CellBlocker {
  kind: 'cell';
  cas: string;
  componentId: string;
  overall: VerdictStatus;
  raw: string;
}
export interface MessageBlocker {
  kind: 'message';
  text: string;
  raw: string;
}
export type ParsedBlocker = CellBlocker | MessageBlocker;

const CELL_RE = /^unreviewed:\s*(.+?)\|(.+?)\s*\(([^)]+)\)\s*$/;

export function parseBlocker(raw: string): ParsedBlocker {
  const m = CELL_RE.exec(raw);
  if (m) {
    const overall = m[3] as VerdictStatus;
    // Trust the wire, but do not invent a status the enum doesn't have.
    if (VERDICT_SEVERITY.includes(overall)) {
      return { kind: 'cell', cas: m[1].trim(), componentId: m[2].trim(), overall, raw };
    }
  }
  // "incomplete: …" and anything unrecognised become a plain message, humanised a little.
  const text = raw.startsWith('incomplete:') ? raw.slice('incomplete:'.length).trim() : raw;
  return { kind: 'message', text, raw };
}

export const cellBlockerKey = (b: CellBlocker) => `${b.cas}|${b.componentId}`;
