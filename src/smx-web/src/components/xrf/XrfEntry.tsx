/**
 * The screen around the XRF grid — the only door physics data has into the record.
 *
 * The old project-creation form used to carry `elementPools[]`, and removing it removed the only way
 * a physicist's measurement could reach a project. Discovery now genuinely PARKS without one
 * (StageDispatcher: "waiting on the physicist's XRF measurement"), so this component is what lifts
 * that park.
 *
 * It owns all the state and every call; `XrfProposalTable` stays presentational. Two server calls,
 * one of which writes: `parse` reads a file and hands back proposals touching nothing, `confirm` is
 * the single writer. Keeping them apart is what makes the operator's confirmation an act rather than
 * a consequence of having chosen a file.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { NotFound, confirmXrf, getXrfState, parseXrf, xrfTemplateUrl } from '../../api/client';
import type { XrfProposal, XrfState } from '../../api/types';
import { SectionHeader } from '../ui/Primitives';
import { XrfProposalTable } from './XrfProposalTable';

/**
 * `null` while the first read is in flight; `'absent'` when intake has not written constraints;
 * `'unreadable'` when something came back that is not an XrfState at all.
 */
type RecordState = XrfState | 'absent' | 'unreadable' | null;

const message = (e: unknown) => (e instanceof Error ? e.message : String(e));

/**
 * The arrays this component reads. `client.ts` casts every response with `as` and validates nothing,
 * so a backend answering an unexpected shape used to throw out of render here — and this component
 * is mounted in the shell's own column, where a throw takes the entry form down with the summary.
 * A payload that cannot be read is reported; the form above it keeps working, because entering a
 * measurement is exactly what an operator looking at a broken record wants to do.
 */
const readable = (x: XrfState): boolean =>
  Array.isArray(x.components) &&
  Array.isArray(x.elementPools) &&
  Array.isArray(x.measuredBackgrounds);

/**
 * A blank row for the manual grid.
 *
 * `problems: []` — an empty row is not yet WRONG, it is just empty, and pre-loading it with
 * complaints about cells the operator has not reached teaches them to ignore the problem column.
 * The server validates on confirm, and blank component/element/line fail there with a message
 * naming the row.
 *
 * `rowNumber` keeps counting from whatever is already on screen so a server refusal that says
 * "row 4" points at the fourth row the operator can see.
 */
const blankRow = (rows: XrfProposal[], components: string[]): XrfProposal => ({
  rowNumber: rows.length + 1,
  component: components[0] ?? '',
  element: '',
  line: '',
  status: 'V',
  signalNote: null,
  backgroundLevel: null,
  backgroundUnit: 'ppm',
  deviceModel: rows[0]?.deviceModel ?? null,
  deviceLod: null,
  deviceLodUnit: 'ppm',
  problems: [],
});

export function XrfEntry({
  projectId,
  onConfirmed,
}: {
  projectId: string;
  onConfirmed?: () => void;
}) {
  const [record, setRecord] = useState<RecordState>(null);
  /**
   * ONE error slot, deliberately. A load failure, a rejected file and a refused confirmation are
   * three ways of saying "the last thing you did did not happen", and stacking three live alerts
   * makes the operator hunt for which one is about the thing they just pressed.
   */
  const [error, setError] = useState<string | null>(null);
  const [proposals, setProposals] = useState<XrfProposal[] | null>(null);
  const [sheetProblems, setSheetProblems] = useState<string[]>([]);
  const [manual, setManual] = useState(false);
  const [busy, setBusy] = useState(false);

  // Which read is the current one. A late reply from a previous project must not overwrite this one.
  const reqId = useRef(0);

  const load = useCallback(async () => {
    const mine = ++reqId.current;
    try {
      const r = await getXrfState(projectId);
      if (mine !== reqId.current) return;
      setRecord(r === NotFound ? 'absent' : readable(r) ? r : 'unreadable');
    } catch (e) {
      if (mine !== reqId.current) return;
      setError(message(e));
    }
  }, [projectId]);

  useEffect(() => {
    setRecord(null);
    setProposals(null);
    setSheetProblems([]);
    setManual(false);
    setError(null);
    void load();
  }, [load]);

  const components =
    record && record !== 'absent' && record !== 'unreadable' ? record.components : [];

  const upload = useCallback(
    async (file: File | undefined) => {
      if (!file) return;
      setBusy(true);
      setError(null);
      setSheetProblems([]);
      try {
        const parsed = await parseXrf(projectId, file);
        setProposals(parsed.proposals);
        setSheetProblems(parsed.sheetProblems);
        setManual(false);
      } catch (e) {
        // A rejected file has to be STATED. Silence after an upload reads as "it worked", and an
        // empty grid reads as "your file had no rows in it" — neither is what happened.
        setProposals(null);
        setError(message(e));
      } finally {
        setBusy(false);
      }
    },
    [projectId],
  );

  const confirm = useCallback(
    async (rows: XrfProposal[]) => {
      setBusy(true);
      setError(null);
      try {
        await confirmXrf(projectId, rows);
        setProposals(null);
        setSheetProblems([]);
        setManual(false);
        // Re-READ rather than patch: the server just changed the record, it decides what the pool
        // and the device now are, and Discovery's park lifts on ITS write, not on ours.
        await load();
        onConfirmed?.();
      } catch (e) {
        // Verbatim. The server's refusal names the row and the rule; a paraphrase loses both.
        setError(message(e));
      } finally {
        setBusy(false);
      }
    },
    [projectId, load, onConfirmed],
  );

  const startManual = () => {
    setError(null);
    setSheetProblems([]);
    setProposals((rows) => (rows && rows.length > 0 ? rows : [blankRow([], components)]));
    setManual(true);
  };

  const addRow = () =>
    setProposals((rows) => [...(rows ?? []), blankRow(rows ?? [], components)]);

  /*
   * The grid is ten columns of transcription and it lives in a 390px column, so it scrolls
   * SIDEWAYS inside its own box. Without this the table would simply be wider than the column and
   * spill over the artifact beside it — and the columns it would spill are the ones carrying the
   * signal note and the Remove control, i.e. the end of every row.
   */
  const table = proposals && (
    <div style={{ overflowX: 'auto' }}>
      <XrfProposalTable
        proposals={proposals}
        components={components}
        onChange={setProposals}
        onConfirm={(rows) => void confirm(rows)}
        busy={busy}
      />
    </div>
  );

  return (
    /*
     * This is the work area's LEFT column on the Background stage — the position every other stage
     * gives its agent. Background has none (the XRF filter is a deterministic pass-through), so the
     * operator's own transcription takes it: the input surface goes where input lives. It is
     * therefore framed as a panel, not as a `.screen` card with a page title over it; the stage
     * header and the stepper already say which screen this is.
     */
    <section aria-label="XRF measurement">
      <header style={{ marginBottom: 'var(--s3)' }}>
        <h3 className="sec__title" style={{ margin: 0 }}>
          XRF measurement
        </h3>
        <p className="small secondary" style={{ margin: '2px 0 0' }}>
          The physicist measures; you transcribe and confirm.
        </p>
      </header>

      {/* The one error slot. */}
      {error && (
        <div className="banner danger" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div className="data small">{error}</div>
        </div>
      )}

      <SectionHeader eyebrow="On the record" />
      {record === null ? (
        <div className="tiny muted" style={{ marginBottom: 14 }}>
          <i className="ti ti-loader" data-running="" aria-hidden="true" /> Reading the project
          record…
        </div>
      ) : (
        <ConfirmedSummary record={record} />
      )}

      <SectionHeader eyebrow="Enter a measurement" />
      {/* Stacked, not a wrapping row: at this column's width a row of a file picker and two
          buttons wraps into ragged lines whose order stops matching the order they are used in. */}
      <div
        className="region"
        style={{ marginBottom: 14, display: 'flex', flexDirection: 'column', gap: 'var(--s2)' }}
      >
        <label htmlFor="xrf-file" className="small secondary">
          Upload the physicist&rsquo;s result file
        </label>
        <input
          id="xrf-file"
          type="file"
          accept=".csv,.tsv,.txt"
          disabled={busy}
          style={{ maxWidth: '100%' }}
          onChange={(e) => {
            void upload(e.target.files?.[0]);
            // Let the same file be chosen twice — after a rejected parse the operator fixes the
            // file and picks it again, and an unchanged value fires no change event.
            e.target.value = '';
          }}
        />
        <div style={{ display: 'flex', gap: 'var(--s2)', flexWrap: 'wrap' }}>
          {/* The template lives on the API, beside the parser, so the two cannot drift apart. */}
          <a className="btn" href={xrfTemplateUrl} download>
            <i className="ti ti-download" aria-hidden="true" /> CSV template
          </a>
          {!manual && (
            <button type="button" className="btn" onClick={startManual}>
              <i className="ti ti-table-plus" aria-hidden="true" /> Enter rows by hand
            </button>
          )}
          {busy && <span className="tiny muted">Working…</span>}
        </div>
        <div className="tiny muted">
          Nothing is written until you confirm. Reading a file only proposes rows — you check them
          first.
        </div>
      </div>

      {/* The parser's findings about the FILE, as opposed to a row. Not an alert: the rows below
          are still usable, and the operator is being told what the file did not say. */}
      {sheetProblems.length > 0 && (
        <div className="banner warn" role="note">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The file raised {sheetProblems.length} problem{sheetProblems.length === 1 ? '' : 's'}.</b>
            {sheetProblems.map((p) => (
              <div key={p} style={{ marginTop: 3 }}>
                {p}
              </div>
            ))}
          </div>
        </div>
      )}

      {/*
        The manual grid is the SAME table, not a second one. One editing surface means one set of
        rules — a second grid is where the L-needs-a-signal-note check would quietly not be.
      */}
      {manual ? (
        <div data-testid="xrf-manual-grid">
          <SectionHeader
            eyebrow="By hand"
            hint="for a result that will not parse — the same checks apply"
          />
          {table}
          <div style={{ marginTop: 10 }}>
            <button type="button" className="btn" onClick={addRow}>
              <i className="ti ti-plus" aria-hidden="true" /> Add a row
            </button>
          </div>
        </div>
      ) : (
        table
      )}
    </section>
  );
}

/**
 * What the record already holds — rendered whether or not there is anything in it.
 *
 * The empty case is not a blank panel. An operator who sees nothing here cannot tell "no measurement
 * has been entered" from "this screen has nothing to do with me", and the second reading is how a
 * project sits parked for a week.
 */
function ConfirmedSummary({ record }: { record: XrfState | 'absent' | 'unreadable' }) {
  /* Not the same statement as "nothing is recorded". Saying that about a payload we could not read
     would report an absence we have not established. */
  if (record === 'unreadable') {
    return (
      <div className="region" data-testid="xrf-confirmed-summary" style={{ marginBottom: 14 }}>
        <div className="small secondary">
          <i className="ti ti-alert-triangle" aria-hidden="true" /> The recorded measurement came
          back in a shape this cannot read, so what is on the record is not shown. Entering a
          measurement still works.
        </div>
      </div>
    );
  }

  if (record === 'absent') {
    return (
      <div className="region" data-testid="xrf-confirmed-summary" style={{ marginBottom: 14 }}>
        <div className="small secondary">
          No XRF background has been recorded — this project has no constraints on the record yet, so
          there is nothing to compare a measurement against. Intake writes them when it runs.
        </div>
      </div>
    );
  }

  const byComponent = record.components.length > 0
    ? record.components
    : [...new Set(record.elementPools.map((p) => p.component))];
  const pooled = byComponent
    .map((c) => ({ component: c, pool: record.elementPools.filter((p) => p.component === c) }))
    .filter((g) => g.pool.length > 0);

  return (
    <div className="region" data-testid="xrf-confirmed-summary" style={{ marginBottom: 14 }}>
      {record.elementPools.length === 0 ? (
        <div className="small secondary">
          No XRF background has been recorded on this project yet. Discovery is waiting on it — it
          screens candidate elements against the measured background, and there is none to screen
          against.
        </div>
      ) : (
        <>
          <div className="small secondary" style={{ marginBottom: 8 }}>
            Confirming again REPLACES this — it is a re-measure, not an addition.
          </div>
          {pooled.map((g) => (
            <div key={g.component} style={{ marginBottom: 6 }}>
              <span className="tiny muted">{g.component}</span>{' '}
              {g.pool.map((p) => (
                <span
                  className={`chip ${p.status === 'V' ? 'v' : 'l'}`}
                  key={`${p.element}-${p.line}`}
                  style={{ marginRight: 3 }}
                  title={p.signalNote ?? undefined}
                >
                  {p.element} {p.line}
                </span>
              ))}
            </div>
          ))}
          <div className="tiny muted" style={{ marginTop: 6 }}>
            {record.elementPools.length} element
            {record.elementPools.length === 1 ? '' : 's'} in the pool ·{' '}
            {record.measuredBackgrounds.length} measured background
            {record.measuredBackgrounds.length === 1 ? '' : 's'}
            {record.device?.model ? (
              <>
                {' '}
                · measured on <span className="data">{record.device.model}</span>
              </>
            ) : null}
          </div>
        </>
      )}
    </div>
  );
}
