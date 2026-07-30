import { useRef, useState } from 'react';
import { fetchSds, uploadSds } from '../api/client';
import type { SdsEnsureResult } from '../api/types';

/**
 * The two things an operator can now do about a safety sheet: go and get one, or supply one.
 *
 * Neither is a gate. Nothing here approves anything — this replaces the MSDS review signature
 * (design 2026-07-29, D8), and it is deliberately the opposite kind of control. The signature asked
 * the operator to attest to a document the system already held; these ask the system to go and
 * obtain one it does not. MSDS-before-order survives untouched behind both: procurement still
 * refuses a substance with no sheet, and no button here can talk it round.
 *
 * The fetch answer is rendered in full, including `attempted[]`. "Could not get it" is a dead end;
 * "tried these four hosts, and each one served something that was not a sheet for this CAS" is the
 * beginning of an operator knowing which supplier to go and ask.
 *
 * One component for three surfaces (registry rows, library gap rows, the reader's unavailable
 * banner) because the pending/failed/succeeded state machine is the fiddly part, and three copies
 * of it would drift into three different answers to "did that work?".
 */
export function SdsActions({
  cas,
  hasSheet,
  onDone,
  collapsed = false,
}: {
  cas: string;
  /** A row that already has a sheet offers to REFRESH it — a revision may have been published. */
  hasSheet: boolean;
  /** Called after anything that could have changed the record, so the surface can re-read. */
  onDone?: () => void;
  /**
   * Start as ONE control that opens the pair, rather than the pair itself.
   *
   * The document library repeats this component down a list — 163 rows on the deployed app, of
   * which 41 were gaps, each printing the identical `Fetch now` + `Upload`. Two controls repeated
   * 41 times are not two affordances, they are wallpaper: the eye stops reading them and the list
   * stops having a shape. Collapsed, a row carries one quiet control and the list can be scanned.
   *
   * It hides nothing: opening it yields exactly the same two controls, so the ability to act on a
   * single substance — including the upload fallback, which is the only exit for a sheet no host
   * will serve — survives intact. The registry, whose rows are a table with a column set aside for
   * this, leaves it expanded.
   */
  collapsed?: boolean;
}) {
  const [busy, setBusy] = useState<'fetch' | 'upload' | null>(null);
  const [result, setResult] = useState<SdsEnsureResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);
  const [open, setOpen] = useState(!collapsed);

  async function runFetch() {
    setBusy('fetch');
    setResult(null);
    setError(null);
    try {
      const r = await fetchSds(cas);
      setResult(r);
      // Re-read whatever the sheet's arrival changed, but only when something DID arrive. Re-reading
      // after "unavailable" would blank the attempt list the operator is reading.
      if (r.status !== 'unavailable') onDone?.();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(null);
    }
  }

  if (!open) {
    return (
      <div className="sds-actions">
        <button
          type="button"
          className="btn btn--quiet"
          onClick={() => setOpen(true)}
          aria-expanded={false}
          title={
            hasSheet
              ? 'Look for a newer revision of this sheet, or supply one yourself.'
              : 'Search for this substance’s safety data sheet, or upload one you already hold. Nothing is signed by doing this.'
          }
        >
          <i className="ti ti-file-search" aria-hidden="true" />
          {hasSheet ? 'Sheet actions' : 'Get sheet'}
        </button>
      </div>
    );
  }

  return (
    <div className="sds-actions">
      <div className="msds-actions">
        <button
          type="button"
          className="btn btn--quiet"
          onClick={() => void runFetch()}
          disabled={busy !== null}
          title={
            hasSheet
              ? 'Look for a newer revision of this sheet and index it if one exists.'
              : 'Search for this substance’s safety data sheet and index it. Nothing is signed by doing this.'
          }
        >
          <i className="ti ti-download" aria-hidden="true" />
          {busy === 'fetch' ? 'Fetching…' : hasSheet ? 'Refresh' : 'Fetch now'}
        </button>

        <button
          type="button"
          className="btn btn--quiet"
          onClick={() => setUploading((u) => !u)}
          disabled={busy !== null}
          aria-expanded={uploading}
        >
          <i className="ti ti-upload" aria-hidden="true" />
          Upload
        </button>
      </div>

      {uploading && (
        <UploadForm
          cas={cas}
          busy={busy === 'upload'}
          onSubmit={async (file, supplier, revisionDate) => {
            setBusy('upload');
            setResult(null);
            setError(null);
            try {
              const r = await uploadSds(cas, file, supplier, revisionDate);
              if (r.ok) {
                setUploading(false);
                onDone?.();
              } else {
                // The validator's verdict, not ours. An upload is a fallback, never an override:
                // a document that is not a safety sheet for this CAS does not become one by
                // arriving through a file picker.
                setError(r.reason ?? 'The file was not accepted as a safety sheet for this CAS.');
              }
            } catch (err: unknown) {
              setError(err instanceof Error ? err.message : String(err));
            } finally {
              setBusy(null);
            }
          }}
        />
      )}

      {error && <p className="tiny danger sds-actions__note">{error}</p>}

      {result && <FetchOutcome result={result} />}
    </div>
  );
}

/**
 * The one control that belongs to the GROUP rather than to a row: go and get every missing sheet.
 *
 * A list of 41 gaps, each offering "fetch this one", asks the operator to press 41 buttons and
 * remember which of them answered. The gap is a property of the group — "41 substances cannot be
 * ordered" — so the bulk attempt belongs beside that sentence, and the per-row control stays for
 * the substance the operator has a particular reason to chase.
 *
 * Sequential on purpose: these are outbound fetches against third-party supplier hosts, and 41 of
 * them in parallel is indistinguishable from an attack. `Stop` is offered because a run over a long
 * list is minutes of work, and an operator who changes their mind should not have to close the tab.
 *
 * The summary counts what actually came back. It is never a "done" — a sheet that no host would
 * serve is still missing, and the reloaded list is what says so.
 */
export function FetchAllSheets({
  cases,
  onDone,
}: {
  /** The CAS numbers this group is missing, served by the backend — never parsed out of a label. */
  cases: readonly string[];
  onDone?: () => void;
}) {
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const [outcome, setOutcome] = useState<{
    obtained: number;
    unavailable: number;
    errored: number;
  } | null>(null);
  const stop = useRef(false);

  async function runAll() {
    stop.current = false;
    setOutcome(null);
    setProgress({ done: 0, total: cases.length });
    let obtained = 0;
    let unavailable = 0;
    let errored = 0;
    let done = 0;
    for (const cas of cases) {
      if (stop.current) break;
      try {
        const r = await fetchSds(cas);
        if (r.status === 'unavailable') unavailable += 1;
        else obtained += 1;
      } catch {
        errored += 1;
      }
      done += 1;
      setProgress({ done, total: cases.length });
    }
    setProgress(null);
    setOutcome({ obtained, unavailable, errored });
    // Re-read only when something arrived. A run that obtained nothing changed no record, and
    // re-reading would blank the summary the operator is still reading.
    if (obtained > 0) onDone?.();
  }

  // No CAS, no request that could be made. An enabled button here would be a promise we cannot keep.
  if (cases.length === 0) return null;

  return (
    <span className="msds-actions">
      <button
        type="button"
        className="btn btn--quiet"
        onClick={() => void runAll()}
        disabled={progress !== null}
        title={`Ask for each of these ${cases.length} sheets in turn, one at a time. Nothing is signed by doing this.`}
      >
        <i className="ti ti-download" aria-hidden="true" />
        {progress ? `Fetching ${progress.done} of ${progress.total}…` : `Fetch all ${cases.length}`}
      </button>
      {progress !== null && (
        <button type="button" className="btn btn--quiet" onClick={() => (stop.current = true)}>
          Stop
        </button>
      )}
      {outcome && (
        <span className="tiny muted" role="status">
          {outcome.obtained} obtained · {outcome.unavailable} still unavailable
          {outcome.errored > 0 ? ` · ${outcome.errored} errored` : ''}
        </span>
      )}
    </span>
  );
}

function FetchOutcome({ result }: { result: SdsEnsureResult }) {
  if (result.status !== 'unavailable') {
    return (
      <p className="tiny muted sds-actions__note">
        {result.status === 'already-had'
          ? 'A current sheet was already indexed.'
          : `Sheet fetched and indexed${result.supplier ? ` from ${result.supplier}` : ''}.`}
      </p>
    );
  }

  const attempts = result.attempted ?? [];
  return (
    <div className="tiny sds-actions__note">
      <b>No sheet could be obtained.</b>
      {result.reason && <span> {result.reason}</span>}
      {/*
        The attempt list is the whole point of the contract. "Unavailable" alone tells the operator
        nothing they can act on; a host that served a PDF for the wrong CAS, and a host that timed
        out, are two different problems with two different next moves.
      */}
      {attempts.length > 0 && (
        <ul className="sds-attempts">
          {attempts.map((a) => (
            <li key={`${a.url}|${a.outcome}`}>
              <span className="muted">{a.supplier || a.url}</span> — {a.outcome}
            </li>
          ))}
        </ul>
      )}
      {attempts.length === 0 && (
        <span className="muted"> Nothing was tried — no candidate source was found.</span>
      )}
    </div>
  );
}

/**
 * Supplier and revision date are asked for, not guessed.
 *
 * With the CAS they are the sheet's identity in the registry (`DedupKey.ForRegistry`), and the
 * backend refuses an upload missing either. That refusal is not pedantry: a sheet stored with a
 * blank segment gets an id nothing can decode, so it would be ingested, listed, and permanently
 * un-openable — the worst of the three possible outcomes, because it looks like coverage.
 */
function UploadForm({
  cas,
  busy,
  onSubmit,
}: {
  cas: string;
  busy: boolean;
  onSubmit: (file: File, supplier: string, revisionDate: string) => void | Promise<void>;
}) {
  const fileRef = useRef<HTMLInputElement>(null);
  const [supplier, setSupplier] = useState('');
  const [revisionDate, setRevisionDate] = useState('');
  const [complaint, setComplaint] = useState<string | null>(null);

  return (
    <form
      className="sds-upload"
      onSubmit={(e) => {
        e.preventDefault();
        const file = fileRef.current?.files?.[0];
        if (!file) return setComplaint('Choose a PDF to upload.');
        if (!supplier.trim() || !revisionDate.trim())
          return setComplaint('A supplier and a revision date are part of the sheet’s identity.');
        setComplaint(null);
        void onSubmit(file, supplier.trim(), revisionDate.trim());
      }}
    >
      <label className="tiny muted">
        Safety sheet (PDF)
        <input ref={fileRef} type="file" accept="application/pdf,.pdf" aria-label={`Safety sheet PDF for ${cas}`} />
      </label>
      <label className="tiny muted">
        Supplier
        <input
          type="text"
          value={supplier}
          onChange={(e) => setSupplier(e.target.value)}
          aria-label={`Supplier for ${cas}`}
        />
      </label>
      <label className="tiny muted">
        Revision date
        <input
          type="text"
          placeholder="YYYY-MM-DD"
          value={revisionDate}
          onChange={(e) => setRevisionDate(e.target.value)}
          aria-label={`Revision date for ${cas}`}
        />
      </label>
      <button type="submit" className="btn btn--quiet" disabled={busy}>
        {busy ? 'Uploading…' : 'Upload sheet'}
      </button>
      {complaint && <p className="tiny danger">{complaint}</p>}
    </form>
  );
}
