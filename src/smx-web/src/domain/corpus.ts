/**
 * Regulatory corpus freshness.
 *
 * Spec §4.4 makes the currency of the corpus SMX's own responsibility: a monthly
 * Regulatory Sync, and every regulatory verdict cites its source *plus the sync date*.
 * That makes corpus age a property of the whole instrument, which is why the masthead
 * carries it — an instrument tells you when it was last calibrated.
 *
 * **But no endpoint reports it.** The sync runs in `src/Smx.Functions` (the regsync
 * Function App); the backend API exposes nothing that says when it last completed. The
 * only sync date in this frontend lives in `mocks/fixtures/regulatory.json`, and it is
 * fixture data.
 *
 * So the masthead does NOT print that date. Laundering a fixture value into the app
 * chrome — where it would appear unbadged, above every screen, in the most authoritative
 * position in the interface — is exactly the failure this codebase is built to prevent:
 * a fabricated value passing for a real one. The stakes are not abstract. An operator who
 * trusts a stale-but-plausible corpus date approves a marker against regulations that
 * have since changed.
 *
 * The slot is therefore rendered *unfilled*, and says so. That follows the same rule as
 * `.stat--absent` and `ParkSlot`: showing the hole and naming it is more honest than
 * quietly dropping the question. When a `GET /corpus` (or a field on an existing
 * response) exists, this becomes a data swap, not a redesign.
 */
export const CORPUS_SYNCED_AT: string | null = null;

export const CORPUS_UNKNOWN_REASON =
  'No endpoint reports the regulatory corpus sync date. The monthly sync runs in the ' +
  'regsync Function App, but the API does not expose its last-completed time — so this ' +
  'is left blank rather than filled from a fixture.';
