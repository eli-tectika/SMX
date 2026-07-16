import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { NotFound, getCost, getMsdsRegistry } from '../../api/client';
import type { CostDoc, MsdsEntry, SupplierAudit } from '../../api/types';
import { Loading } from '../../components/Loading';
import { StageStatusCard } from '../../components/StageStatusCard';
import { Data } from '../../components/ui/Data';
import { CitationChip, EmptyState, SectionHeader, StatCard } from '../../components/ui/Primitives';
import type { ScreenProps } from '../ProjectLayout';

const usd = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 });

/**
 * Cost & availability (spec §4.6) — the per-substance supplier audit, real.
 *
 * This screen's whole job is to NOT invent a number. `bestQuote` is absent whenever nothing parseable was
 * on file — price is free text on a minority of catalog products — and `priceNote` says so in words.
 * Nothing is interpolated, averaged, or currency-converted into existence, because procurement cuts a PO
 * against this figure. So an absence renders AS an absence, never as a zero or a dash that reads like one.
 *
 * Two things the fixture used to show that simply do not exist in the record, and are therefore gone:
 * a per-supplier price comparison (`suppliers` is names only; there is exactly one best quote) and lead
 * time (Cost has no such concept at all). Drawing either would have been the exact failure this screen
 * exists to prevent.
 *
 * The MSDS registry is joined in on CAS. Spec §5 makes a current, reviewed sheet a hard precondition for
 * an order, and this is the screen where the order is decided — a substance whose sheet is missing should
 * say so here, not only on a registry page nobody is looking at. Note the system does not itself block
 * procurement (that gate is not built): this states the rule against the real record.
 */
export function Cost({ project }: ScreenProps) {
  const stage = project.stages.cost;
  const status = stage?.status;

  const [doc, setDoc] = useState<CostDoc | null>(null);
  const [sheets, setSheets] = useState<MsdsEntry[]>([]);
  /**
   * Whether the MSDS read itself failed — which is NOT the same as "no sheet on file", and the difference
   * matters: telling the operator a substance has no safety sheet when we simply could not check is a
   * fabricated claim about an absence, on the screen where an order gets decided.
   */
  const [sheetsFailed, setSheetsFailed] = useState(false);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [errMsg, setErrMsg] = useState<string>();

  const load = useCallback(
    async (signal?: { cancelled: boolean }) => {
      // The two reads fail INDEPENDENTLY on purpose. The MSDS registry is a supplementary layer over the
      // audit — losing it must not blank the prices, which are this screen's actual subject. Joining them
      // in one Promise.all meant a registry hiccup took the whole audit down with it.
      const sheets = getMsdsRegistry().catch(() => {
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
  const sheetFor = (cas: string) => sheets.find((s) => s.cas === cas);
  // Unknown when the registry did not load: we only assert "not orderable" from a sheet we actually read.
  const orderable = (cas: string) => (sheetsFailed ? true : sheetFor(cas)?.reviewStatus === 'reviewed');

  if (phase === 'loading') return <Loading what="the cost audit" />;

  if (phase === 'error') {
    return (
      <section className="screen">
        <Head />
        <div className="banner danger">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>Could not load the cost audit.</b>
            <div style={{ marginTop: 3 }}>{errMsg}</div>
          </div>
        </div>
      </section>
    );
  }

  if (phase === 'absent' || !doc) {
    return (
      <section className="screen">
        <Head />
        <StageStatusCard name="Cost audit" state={stage} />
        <EmptyState
          icon="ti-package-off"
          title="Nothing costed yet."
          body={
            <>
              Cost prices the finalized codes, so it runs after dosing produces them. It is a deterministic
              catalog lookup — there is no agent here to ask and nothing to revise.
            </>
          }
        />
      </section>
    );
  }

  const quoted = doc.substances.filter((s) => s.bestQuote);
  const unquoted = doc.substances.length - quoted.length;
  const singleSource = doc.substances.filter((s) => s.risks.includes('single-source')).length;
  const blocked = doc.substances.filter((s) => !orderable(s.cas)).length;

  return (
    <section className="screen">
      <Head generatedAt={doc.generatedAt} />
      <StageStatusCard name="Cost audit" state={stage} />

      <div className="stat-strip">
        <StatCard label="Substances audited" value={doc.substances.length} hint="distinct CAS in the codes" />
        <StatCard
          label="Quoted"
          value={`${quoted.length} of ${doc.substances.length}`}
          tone={unquoted > 0 ? 'warning' : undefined}
          hint={unquoted > 0 ? `${unquoted} need a quote` : 'every substance has a price'}
        />
        <StatCard
          label="Single-source"
          value={singleSource}
          tone={singleSource > 0 ? 'warning' : undefined}
          hint="no second supplier on file"
        />
        <StatCard
          label="Not orderable"
          value={blocked}
          tone={blocked > 0 ? 'danger' : undefined}
          hint="no reviewed MSDS"
        />
      </div>

      {sheetsFailed && (
        <div className="banner warn">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            The MSDS registry did not load, so the safety-sheet status below is <b>unknown</b> — not
            cleared. Spec §5 still makes a reviewed sheet a precondition for an order; check the registry
            before ordering anything here.
          </div>
        </div>
      )}

      {doc.substances.map((s) => (
        <SubstanceAudit
          key={`${s.cas}|${s.element}`}
          audit={s}
          sheet={sheetFor(s.cas)}
          sheetUnknown={sheetsFailed}
        />
      ))}
    </section>
  );
}

function SubstanceAudit({
  audit,
  sheet,
  sheetUnknown,
}: {
  audit: SupplierAudit;
  sheet: MsdsEntry | undefined;
  /** The registry read failed — say nothing about this substance's sheet rather than guess. */
  sheetUnknown: boolean;
}) {
  const q = audit.bestQuote;
  const reviewed = sheetUnknown || sheet?.reviewStatus === 'reviewed';

  return (
    <div style={{ marginBottom: 'var(--s5)' }}>
      <SectionHeader
        title={audit.element}
        hint={audit.cas}
        actions={
          <>
            {!reviewed && (
              <span className="chip x" title="Spec §5 — a reviewed MSDS is a hard precondition for an order">
                <i className="ti ti-file-alert" aria-hidden="true" />
                &nbsp;{sheet ? `MSDS ${sheet.reviewStatus}` : 'no MSDS on file'}
              </span>
            )}
            {audit.risks.map((r) => (
              <span className="chip n" key={r}>
                <i className="ti ti-alert-triangle" aria-hidden="true" />
                &nbsp;{r}
              </span>
            ))}
          </>
        }
      />

      <div className="region">
        {q ? (
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, flexWrap: 'wrap' }}>
            <Data kind="price">
              <span style={{ fontSize: 17, fontWeight: 600 }}>
                {q.currency} {usd.format(q.usdPerGram)}
              </span>
            </Data>
            {/* Per GRAM. The record has no per-kg figure and this screen does not derive one. */}
            <span className="tiny muted">per gram</span>
            <span className="small secondary">
              {q.supplier} · <span className="data">{q.pack}</span>
            </span>
            {/* Procurement acts on this number, so the listing it came from travels with it. */}
            <span style={{ marginLeft: 'auto' }}>
              <CitationChip {...q.citation} />
            </span>
          </div>
        ) : (
          // The absence IS the answer. A zero here would be a fabricated price.
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            <StatCard label="Price" absent hint="nothing parseable on file" />
            <span className="small secondary" style={{ flex: 1 }}>
              {audit.priceNote}
            </span>
          </div>
        )}

        <div className="tiny muted" style={{ marginTop: 10 }}>
          {audit.suppliers.length > 0 ? (
            <>
              Suppliers on file:{' '}
              {audit.suppliers.map((n) => (
                <span className="chip chip--neutral" key={n} style={{ marginRight: 3 }}>
                  {n}
                </span>
              ))}
              {/* Names only — the catalog carries no per-supplier price, so there is nothing to rank. */}
            </>
          ) : (
            <>No supplier on file for this substance.</>
          )}
        </div>
      </div>

      {!reviewed && (
        <div className="banner danger" style={{ margin: '8px 0 0' }}>
          <i className="ti ti-file-alert" aria-hidden="true" />
          <div>
            <b>Not orderable yet.</b> Spec §5 makes a reviewed safety data sheet a hard precondition for an
            order.{' '}
            {sheet ? (
              <>
                The sheet for <span className="data">{audit.cas}</span> is <b>{sheet.reviewStatus}</b>, not
                reviewed.
              </>
            ) : (
              <>
                No sheet is on file for <span className="data">{audit.cas}</span>.
              </>
            )}{' '}
            <Link to="/msds-registry">Open the MSDS registry →</Link>
          </div>
        </div>
      )}
    </div>
  );
}

function Head({ generatedAt }: { generatedAt?: string }) {
  return (
    <div className="cap">
      <b>Cost &amp; availability</b>
      &nbsp;·&nbsp; spec §4.6 — supplier audit per substance
      {generatedAt && <span className="muted"> · priced {generatedAt.slice(0, 10)}</span>}
    </div>
  );
}
