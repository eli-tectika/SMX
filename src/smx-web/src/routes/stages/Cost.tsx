import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { NotFound, getCost, getMsdsRegistry } from '../../api/client';
import type { CostDoc, MsdsEntry, PriceQuote, SupplierAudit } from '../../api/types';
import { Loading } from '../../components/Loading';
import { Data } from '../../components/ui/Data';
import { CitationChip, EmptyState, SectionHeader, StatCard } from '../../components/ui/Primitives';
import type { ScreenProps } from '../ProjectLayout';

const usd = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 });

/**
 * What the registry says about one substance's safety data sheet. Four states, and `unknown` is the
 * load-bearing one.
 *
 * A sheet ON FILE is a hard precondition for a purchase order, so this screen must be able to say
 * "no sheet" — but only from a registry it actually read. Telling the operator a substance has no
 * safety sheet when we merely could not check is a fabricated claim about an absence, made on the
 * screen where the order is decided. So a registry that did not load yields `unknown` for every
 * substance, and `unknown` is never folded into either "blocked" or "cleared".
 *
 * The predicate is the SHEET'S EXISTENCE, not a signature over it: the review signature was deleted
 * in the 2026-07-29 design (D8/D9) and `MsdsEntry.reviewStatus` went with it. `no-sheet` is the row
 * that survives that change — a governance-only entry with no `documentId`, which has a registry
 * line but nothing behind it, and which the backend's order endpoint refuses exactly as if the row
 * were absent. It is kept distinct from `missing` because the two send the operator to different
 * places: one sheet has to be obtained, the other has to be linked.
 */
type SheetState =
  | { kind: 'unknown' }
  | { kind: 'on-file' }
  | { kind: 'no-sheet' }
  | { kind: 'missing' };

/** The two states that stop a purchase order. `unknown` is deliberately not one of them. */
const blocksOrder = (s: SheetState) => s.kind === 'no-sheet' || s.kind === 'missing';

/**
 * A price, or a stated reason there is none. Three cases, because two would lose one of them.
 *
 * `bestQuote` is ABSENT whenever nothing parseable was on file — price is free text on a minority of
 * catalog products — and `priceNote` says so in the record's own words. Nothing is interpolated,
 * averaged or currency-converted into existence, because procurement cuts a PO against this figure.
 * So an absence renders AS an absence, never as a zero or a dash that reads like one.
 *
 * `unreadable` is the third case: a quote on the record whose figure is not a finite number. It must
 * not fall through to the absence path either, because `priceNote` would then explain the wrong
 * thing — and it must certainly not be formatted, since `Intl` renders a non-number as `NaN` and a
 * missing one as `0`, both of which would be a fabricated price on the line that gets ordered.
 */
type Price = { kind: 'quote'; quote: PriceQuote } | { kind: 'none' } | { kind: 'unreadable' };

function priceOf(audit: SupplierAudit): Price {
  const q = audit.bestQuote;
  if (q === undefined || q === null) return { kind: 'none' };
  if (typeof q !== 'object') return { kind: 'unreadable' };
  if (typeof q.usdPerGram !== 'number' || !Number.isFinite(q.usdPerGram)) return { kind: 'unreadable' };
  return { kind: 'quote', quote: q };
}

/**
 * Is this row something we can render at all? `client.ts` casts every response with `as` and
 * validates nothing, so a backend that drifts hands this screen whatever it likes. A row without a
 * CAS and an element cannot be identified, and an unidentified price is worse than no price.
 */
function isAudit(row: unknown): row is SupplierAudit {
  if (typeof row !== 'object' || row === null) return false;
  const r = row as Partial<SupplierAudit>;
  return typeof r.cas === 'string' && typeof r.element === 'string';
}

/** Strings only. A `reference` or a `retrievedAt` that is not a string is not a citation. */
function isCitation(c: PriceQuote['citation'] | undefined): boolean {
  return (
    typeof c === 'object' &&
    c !== null &&
    typeof c.source === 'string' &&
    typeof c.reference === 'string' &&
    typeof c.retrievedAt === 'string'
  );
}

/**
 * Cost & availability — what each substance costs, from whom, and whether it can be ordered at all.
 *
 * Procurement cuts a purchase order against these numbers, which sets every rule on this screen:
 *
 *  - **An absence is rendered as an absence.** See `priceOf`.
 *  - **The safety-sheet blocker is the loudest thing here**, because it is the one fact on this
 *    screen that stops a purchase outright. It is stated once, at the top, over every substance it
 *    applies to — one red banner per row taught the eye to skip all of them.
 *  - **"Could not check" is not "cleared".** See `SheetState`.
 *  - **The screen guards its own payload.** A malformed audit degrades this region to a stated
 *    failure rather than throwing; the shell's error boundary is a backstop, not the plan.
 *
 * Two things that do not exist in the record are therefore not drawn: a per-supplier price
 * comparison (`suppliers` is names only — there is exactly one best quote, so there is nothing to
 * rank) and lead time (Cost has no such concept at all). Drawing either would be the exact failure
 * this screen exists to prevent.
 *
 * No analysis agent produced this audit: Cost is a deterministic catalog lookup and its run carries
 * a null agent, so the operator can tell arithmetic from reasoning (PipelineRunner.cs:70). That is
 * narrower than the screen used to claim — there IS an agent to ask, since one chat agent serves
 * every stage — and the difference matters, because "nobody to ask" would send the operator away
 * from the one place they can question a figure.
 */
export function Cost({ project }: ScreenProps) {
  const stage = project.stages.cost;
  const status = stage?.status;

  const [doc, setDoc] = useState<CostDoc | null>(null);
  const [sheets, setSheets] = useState<MsdsEntry[]>([]);
  /**
   * Whether the MSDS read itself failed — which is NOT the same as "no sheet on file", and the
   * difference matters: telling the operator a substance has no safety sheet when we simply could
   * not check is a fabricated claim about an absence, on the screen where an order gets decided.
   */
  const [sheetsFailed, setSheetsFailed] = useState(false);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [errMsg, setErrMsg] = useState<string>();

  const load = useCallback(
    async (signal?: { cancelled: boolean }) => {
      // The two reads fail INDEPENDENTLY on purpose. The MSDS registry is a supplementary layer over
      // the audit — losing it must not blank the prices, which are this screen's actual subject.
      // Joining them in one Promise.all meant a registry hiccup took the whole audit down with it.
      const sheets = getMsdsRegistry()
        .then((rows) => {
          // A 200 whose body is not a list is a FAILED read, not an empty registry. Coercing it to
          // `[]` would report every substance as having no sheet on file — a fabricated absence.
          if (!Array.isArray(rows)) throw new Error('the registry did not come back as a list');
          return rows.filter((r): r is MsdsEntry => typeof r?.cas === 'string');
        })
        .catch(() => {
          setSheetsFailed(true);
          return [] as MsdsEntry[];
        });
      try {
        const c = await getCost(project.projectId);
        if (signal?.cancelled) return;
        if (c === NotFound) {
          setDoc(null);
          setPhase('absent');
        } else {
          setDoc(c);
          setPhase('ready');
        }
      } catch (err) {
        if (!signal?.cancelled) {
          setErrMsg(err instanceof Error ? err.message : String(err));
          setPhase('error');
        }
        return;
      }
      const m = await sheets;
      if (!signal?.cancelled) setSheets(m);
    },
    [project.projectId],
  );

  useEffect(() => {
    const signal = { cancelled: false };
    void load(signal);
    return () => {
      signal.cancelled = true;
    };
  }, [load, status]);

  /** The sheet for a substance, by CAS — the record's own key, not a reconstructed display string. */
  const sheetState = useCallback(
    (cas: string): SheetState => {
      if (sheetsFailed) return { kind: 'unknown' };
      const sheet = sheets.find((s) => s.cas === cas);
      if (!sheet) return { kind: 'missing' };
      return sheet.documentId ? { kind: 'on-file' } : { kind: 'no-sheet' };
    },
    [sheets, sheetsFailed],
  );

  if (phase === 'loading') return <Loading what="the cost audit" />;

  if (phase === 'error') {
    return (
      <section className="screen">
        <SectionHeader title="What each substance costs" headingLevel={3} />
        <div className="banner danger" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div className="prose">
            <b>The cost audit could not be read.</b>
            <div>{errMsg}</div>
          </div>
        </div>
      </section>
    );
  }

  if (phase === 'absent' || !doc) {
    return (
      <section className="screen">
        <SectionHeader title="What each substance costs" headingLevel={3} />
        <EmptyState
          icon="ti-package-off"
          title="Nothing costed yet."
          body="Cost runs once dosing has finalized the codes. It is a deterministic catalog lookup, so there is no analysis to wait on — only the codes."
        />
      </section>
    );
  }

  /*
   * Defensive, and for a reason this plan states plainly: `client.ts` casts with `as` and validates
   * nothing, so `doc.substances` is whatever the wire carried. `.filter` on a non-array threw a
   * TypeError that took the whole screen down. A stated failure in this region is a better answer.
   */
  const rows: unknown[] | null = Array.isArray(doc.substances) ? doc.substances : null;
  const audits: SupplierAudit[] = rows ? rows.filter(isAudit) : [];
  const unreadableRows = rows ? rows.length - audits.length : 0;

  const priced = audits.filter((a) => priceOf(a).kind === 'quote').length;
  const unpriced = audits.length - priced;
  const blocked = audits.filter((a) => blocksOrder(sheetState(a.cas)));

  return (
    <>
      <section className="screen">
        <SectionHeader title="What can be ordered" headingLevel={3} />

        {/*
          Two figures, because these are the two that change what the operator does next: chase a
          price, or chase a safety data sheet. "Substances audited" is carried by "x of y", and
          "single-source" is a property of a row, not a number anybody acts on in aggregate.
        */}
        <div className="stat-strip">
          {/*
            Both tiles go ABSENT when there is nothing to count. "0 of 0 priced — every substance has
            a price" and "0 not orderable" are both clearances, and a screen that could not read its
            own audit has no basis for either.
          */}
          {audits.length === 0 ? (
            <StatCard
              label="Priced"
              absent
              hint={rows === null ? 'the audit could not be read' : 'the audit carries no substances'}
            />
          ) : (
            <StatCard
              label="Priced"
              value={`${priced} of ${audits.length}`}
              tone={unpriced > 0 ? 'warning' : undefined}
              hint={
                unpriced > 0
                  ? `${unpriced} with no price on the record`
                  : 'every substance has a price'
              }
            />
          )}
          {/*
            The count is UNKNOWN when the registry did not load, and a `0` here would read as "none
            blocked" — a clearance this screen has no basis for. So it renders as the absence it is.
          */}
          {sheetsFailed || audits.length === 0 ? (
            <StatCard
              label="Not orderable"
              absent
              hint={
                sheetsFailed
                  ? 'the safety-sheet registry did not load'
                  : 'nothing to check against a safety sheet'
              }
            />
          ) : (
            <StatCard
              label="Not orderable"
              value={blocked.length}
              tone={blocked.length > 0 ? 'danger' : undefined}
              hint="no safety data sheet on file"
            />
          )}
        </div>

        {rows === null && (
          <div className="banner danger" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div className="prose">
              <b>The cost audit came back in a shape this screen cannot read.</b> No prices are shown
              because none could be read — do not take the absence below as "nothing is priced".
            </div>
          </div>
        )}

        {unreadableRows > 0 && (
          <div className="banner warn" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div className="prose">
              {unreadableRows} {unreadableRows === 1 ? 'entry' : 'entries'} in this audit could not be
              read and {unreadableRows === 1 ? 'is' : 'are'} not shown below.
            </div>
          </div>
        )}

        {sheetsFailed ? (
          /*
           * Not cleared — unchecked. Stated where the order is decided, not only on a registry page
           * nobody is looking at, and stated as a gap in OUR knowledge rather than a fact about the
           * substances.
           */
          <div className="banner warn" role="alert">
            <i className="ti ti-help-circle" aria-hidden="true" />
            <div className="prose">
              <b>
                The safety-sheet registry did not load, so every sheet status below is unknown, not
                cleared.
              </b>{' '}
              A safety data sheet on file is a hard precondition for an order, and this screen cannot
              say which substances have one. <Link to="/msds-registry">Check the registry directly</Link>{' '}
              before ordering anything.
            </div>
          </div>
        ) : blocked.length > 0 ? (
          <OrderBlocker blocked={blocked} sheetState={sheetState} />
        ) : audits.length > 0 ? (
          <p className="prose" style={{ margin: 0 }}>
            Every substance below has a safety data sheet on file, so nothing here is blocked on
            safety grounds.
          </p>
        ) : null}
      </section>

      <section className="screen">
        <SectionHeader
          title="What each substance costs"
          headingLevel={3}
          count={audits.length}
          hint="read from the supplier catalog — no analysis agent authored these figures"
        />

        {audits.length === 0 ? (
          <p className="prose" style={{ margin: 0 }}>
            {rows === null
              ? 'Nothing could be read from the audit.'
              : 'The audit carries no substances.'}
          </p>
        ) : (
          /*
           * A ruled ledger, not a stack of boxes. The rule on the container is what separates the
           * heading from the first row — the heading owns a hairline it did not have — and every
           * row after the first carries its own, so two substances can no longer run together.
           * The first row takes none: a doubled rule under the heading is a seam, not a divide.
           */
          <div style={{ borderTop: '1px solid var(--border)' }}>
            {audits.map((a, i) => (
              <SubstanceAudit
                key={`${a.cas}|${a.element}`}
                audit={a}
                sheet={sheetState(a.cas)}
                first={i === 0}
              />
            ))}
          </div>
        )}
      </section>
    </>
  );
}

/**
 * The loudest element on the screen, and it should be: a missing safety data sheet is the one fact
 * here that stops a purchase order outright, whereas a missing price only delays one.
 *
 * `role="alert"` because it arrives on an async read — a mutation a screen reader announces — and an
 * operator who cannot see the hatching gets the same news.
 */
function OrderBlocker({
  blocked,
  sheetState,
}: {
  blocked: SupplierAudit[];
  sheetState: (cas: string) => SheetState;
}) {
  return (
    <div
      className="hatch-danger"
      role="alert"
      data-blocked={blocked.length}
      style={{
        border: '1px solid var(--border-danger)',
        borderRadius: 'var(--r3)',
        padding: 'var(--s4)',
        /*
         * The red is claimed ONCE, here, and everything inside inherits it. `.prose` inside a
         * hatched block defers its colour to the container by design (primitives.css) — which is
         * exactly what makes the per-item `color:` declarations this block used to carry
         * unnecessary. A list that had to repaint every <li> was one forgotten line away from
         * grey text on a red ground.
         */
        color: 'var(--text-danger)',
      }}
    >
      <p
        style={{
          margin: 0,
          fontFamily: 'var(--font-serif)',
          fontSize: 'var(--t-title)',
          fontWeight: 'var(--w-semibold)',
          lineHeight: 'var(--lh-tight)',
        }}
      >
        <i className="ti ti-file-alert" aria-hidden="true" />{' '}
        {blocked.length === 1
          ? '1 substance cannot be ordered.'
          : `${blocked.length} substances cannot be ordered.`}
      </p>
      <p className="prose" style={{ margin: 'var(--s3) 0 0' }}>
        A safety data sheet on file is a hard precondition for an order, and these do not have one:
      </p>
      <ul className="prose" style={{ margin: 'var(--s2) 0 0', paddingLeft: '1.2em' }}>
        {blocked.map((a) => {
          const s = sheetState(a.cas);
          return (
            <li key={`${a.cas}|${a.element}`}>
              <b>{a.element}</b> <Data kind="cas">{a.cas}</Data> —{' '}
              {s.kind === 'no-sheet' ? (
                <>its registry entry has no sheet behind it</>
              ) : (
                <>no sheet is on file</>
              )}
            </li>
          );
        })}
      </ul>
      <p className="prose" style={{ margin: 'var(--s3) 0 0' }}>
        <Link to="/msds-registry">Open the MSDS registry</Link> to obtain or review the sheets.
      </p>
    </div>
  );
}

function SubstanceAudit({
  audit,
  sheet,
  first,
}: {
  audit: SupplierAudit;
  sheet: SheetState;
  /** The row directly under the list's own rule — it must not draw a second one. */
  first: boolean;
}) {
  const price = priceOf(audit);
  // Names only — the catalog carries no per-supplier price, so there is nothing to rank.
  const suppliers = Array.isArray(audit.suppliers)
    ? audit.suppliers.filter((n): n is string => typeof n === 'string')
    : [];
  /*
   * Availability risks, in NEUTRAL. "single-source" is a property of the supply, not a verdict this
   * system reached — amber here would say NeedsReview, which nothing has claimed.
   */
  const risks = Array.isArray(audit.risks)
    ? audit.risks.filter((r): r is string => typeof r === 'string')
    : [];

  return (
    <div
      data-row="substance"
      style={{
        borderTop: first ? undefined : '1px solid var(--border)',
        padding: 'var(--s4) 0',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 'var(--s2)', flexWrap: 'wrap' }}>
        {/* The row's own heading. Nothing below it may be larger or heavier — see the price. */}
        <span style={{ fontSize: 'var(--t-lead)', fontWeight: 'var(--w-medium)' }}>
          {audit.element}
        </span>
        <span className="small muted">
          CAS <Data kind="cas">{audit.cas}</Data>
        </span>
        <span style={{ marginLeft: 'auto', display: 'flex', gap: 'var(--s1)', flexWrap: 'wrap' }}>
          {/*
            The row's own marker for the blocker stated in full above. Red, because "cannot be
            ordered" is a Fail on the record's own terms — but only when we READ the registry:
            `unknown` gets a neutral chip, since the top of the screen has already said we could not
            check and a row with no chip at all would read as cleared by the time it is scrolled to.
          */}
          {sheet.kind === 'missing' && (
            <span className="chip x">
              <i className="ti ti-file-alert" aria-hidden="true" />
              &nbsp;no MSDS on file
            </span>
          )}
          {sheet.kind === 'no-sheet' && (
            <span className="chip x">
              <i className="ti ti-file-alert" aria-hidden="true" />
              &nbsp;no MSDS document
            </span>
          )}
          {sheet.kind === 'unknown' && (
            <span className="chip chip--neutral">MSDS status not checked</span>
          )}
          {risks.map((r) => (
            <span className="chip chip--neutral" key={r}>
              {r}
            </span>
          ))}
        </span>
      </div>

      {price.kind === 'quote' ? (
        <div
          style={{
            display: 'flex',
            alignItems: 'baseline',
            gap: 'var(--s3)',
            flexWrap: 'wrap',
            marginTop: 'var(--s3)',
          }}
        >
          {/*
            A figure that is REFERENCED, not read: it is identified at a glance and acted on, and
            it is already set apart by being the only mono value on the line. It sits at the row
            heading's weight and no higher — a price heavier than the heading of the row it
            belongs to, and heavier than the section title above both, is how a scale inverts.
          */}
          <Data kind="price">
            <span style={{ fontSize: 'var(--t-lead)', fontWeight: 'var(--w-medium)' }}>
              {price.quote.currency} {usd.format(price.quote.usdPerGram)}
            </span>
          </Data>
          {/* Per GRAM. The record has no per-kg figure and this screen does not derive one. */}
          <span className="small muted">per gram</span>
          <span className="small secondary">
            {price.quote.supplier} · <Data kind="code">{price.quote.pack}</Data>
          </span>
          {/* Procurement acts on this number, so the listing it came from travels with it. */}
          {isCitation(price.quote.citation) && (
            <span style={{ marginLeft: 'auto' }}>
              <CitationChip {...price.quote.citation} />
            </span>
          )}
        </div>
      ) : (
        // The absence IS the answer. A zero here would be a fabricated price.
        <div
          style={{
            display: 'flex',
            alignItems: 'baseline',
            gap: 'var(--s3)',
            flexWrap: 'wrap',
            marginTop: 'var(--s3)',
          }}
        >
          <span style={{ fontSize: 'var(--t-lead)', color: 'var(--text-muted)' }}>No price</span>
          {/*
            READ, and the one line on this row that has to be. It is the sentence that stops
            procurement acting on a number that is not there — the record's own words for why
            there is no figure, or the reason the figure could not be read. It sat at the floor in
            muted grey, which is where the eye goes last, next to a "No price" it was explaining.
          */}
          <p className="prose" style={{ flex: 1, margin: 0 }}>
            {price.kind === 'unreadable'
              ? 'A quote is on the record but its figure could not be read, so no price is shown.'
              : typeof audit.priceNote === 'string' && audit.priceNote.length > 0
                ? audit.priceNote
                : 'Nothing parseable was on file, and the record gives no reason.'}
          </p>
        </div>
      )}

      <div className="small muted" style={{ marginTop: 'var(--s3)' }}>
        {suppliers.length > 0 ? (
          <>
            Suppliers on file:{' '}
            {suppliers.map((n) => (
              <span className="chip chip--neutral" key={n} style={{ marginRight: 3 }}>
                {n}
              </span>
            ))}
          </>
        ) : (
          <>No supplier on file for this substance.</>
        )}
      </div>
    </div>
  );
}
