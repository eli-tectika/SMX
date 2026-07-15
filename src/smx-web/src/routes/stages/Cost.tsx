import { Link } from 'react-router-dom';
import { MockBadge } from '../../components/MockBadge';
import { BarRow, EmptyState, SectionHeader, StatCard } from '../../components/ui/Primitives';
import cost from '../../mocks/fixtures/cost.json';
import msds from '../../mocks/fixtures/msds-registry.json';

interface Supplier {
  name: string;
  pricePerKg: number;
  currency: string;
  leadTimeDays: number;
  availability: string;
}
interface Molecule {
  element: string;
  form: string;
  grade: string;
  suppliers: Supplier[];
  supplyRisk: string | null;
}

/**
 * Cost & availability (spec §4.6) — the per-molecule supplier audit.
 *
 * The MSDS registry is joined in here on purpose. Spec §5 makes a current MSDS a
 * hard precondition for an order, and this is the screen where the order is
 * actually decided — a substance whose safety sheet is missing or expired should
 * say so at the moment of choosing a supplier, not only on a registry page nobody
 * is looking at. Both sides are fixtures, so joining them fabricates nothing.
 */
export function Cost() {
  const { molecules } = cost as { molecules: Molecule[] };
  const { entries } = msds as { entries: { substance: string; status: string }[] };

  const msdsStatus = (m: Molecule): string | undefined =>
    entries.find((e) => e.substance.toLowerCase() === `${m.element} ${m.form}`.toLowerCase())?.status;

  if (molecules.length === 0) {
    return (
      <section className="screen" data-provenance="mock">
        <div className="cap">
          <b>Cost &amp; availability</b>
        spec §4.6
        </div>
        <MockBadge />
        <EmptyState icon="ti-package-off" title="No molecules costed yet." />
      </section>
    );
  }

  const cheapest = molecules.map((m) => Math.min(...m.suppliers.map((s) => s.pricePerKg)));
  const basket = cheapest.reduce((a, b) => a + b, 0);
  const worstLead = Math.max(...molecules.flatMap((m) => m.suppliers.map((s) => s.leadTimeDays)));
  const singleSource = molecules.filter((m) => m.suppliers.length === 1).length;
  const blockedOrders = molecules.filter((m) => {
    const st = msdsStatus(m);
    return st !== undefined && st !== 'current';
  }).length;

  const maxPrice = Math.max(...molecules.flatMap((m) => m.suppliers.map((s) => s.pricePerKg)));
  const maxLead = Math.max(...molecules.flatMap((m) => m.suppliers.map((s) => s.leadTimeDays)));

  return (
    <section className="screen" data-provenance="mock">
      <div className="cap">
        <b>Cost &amp; availability</b>
        spec §4.6 — supplier audit per molecule
      </div>

      <MockBadge note="No supplier catalog was queried. Prices, lead times and availability are illustrative." />

      <div className="stat-strip">
        <StatCard
          label="Cheapest basket"
          value={`$${basket.toLocaleString()}`}
          hint="one kg of each, cheapest supplier"
        />
        <StatCard label="Worst lead time" value={`${worstLead}d`} hint="slowest quoted supplier" />
        <StatCard
          label="Single-source"
          value={singleSource}
          tone={singleSource > 0 ? 'warning' : undefined}
          hint="no second supplier found"
        />
        <StatCard
          label="Order blocked"
          value={blockedOrders}
          tone={blockedOrders > 0 ? 'danger' : undefined}
          hint="no current MSDS"
        />
      </div>

      {molecules.map((m) => {
        const minPrice = Math.min(...m.suppliers.map((s) => s.pricePerKg));
        const sheet = msdsStatus(m);
        const blocked = sheet !== undefined && sheet !== 'current';

        return (
          <div key={m.element + m.form} style={{ marginBottom: 22 }}>
            <SectionHeader
              title={`${m.element} ${m.form}`}
              hint={m.grade}
              actions={
                <>
                  {blocked && (
                    <span className="chip x" title="Spec §5 — MSDS is a hard precondition for an order">
                      <i className="ti ti-file-alert" aria-hidden="true" />
                      &nbsp;order blocked — MSDS {sheet}
                    </span>
                  )}
                  {m.suppliers.length === 1 && (
                    <span className="chip n">
                      <i className="ti ti-alert-triangle" aria-hidden="true" />
                      &nbsp;single source
                    </span>
                  )}
                </>
              }
            />

            <div className="region">
              <div className="tiny muted" style={{ marginBottom: 4 }}>
                Price per kg
              </div>
              {m.suppliers.map((s) => (
                <BarRow
                  key={s.name}
                  label={s.name}
                  sub={s.availability}
                  value={s.pricePerKg}
                  max={maxPrice}
                  display={`${s.currency} ${s.pricePerKg.toLocaleString()}`}
                  best={s.pricePerKg === minPrice && m.suppliers.length > 1}
                />
              ))}

              <div className="tiny muted" style={{ margin: '10px 0 4px' }}>
                Lead time
              </div>
              {m.suppliers.map((s) => (
                <BarRow
                  key={s.name}
                  label={s.name}
                  value={s.leadTimeDays}
                  max={maxLead}
                  display={`${s.leadTimeDays} days`}
                />
              ))}
            </div>

            {m.supplyRisk && (
              <div className="banner warn" style={{ margin: '8px 0 0' }}>
                <i className="ti ti-alert-triangle" aria-hidden="true" />
                <div>{m.supplyRisk}</div>
              </div>
            )}

            {blocked && (
              <div className="banner danger" style={{ margin: '8px 0 0' }}>
                <i className="ti ti-file-alert" aria-hidden="true" />
                <div>
                  <b>Procurement blocked.</b> The safety data sheet for {m.element} {m.form} is{' '}
                  <b>{sheet}</b>
        . Spec §5 makes a current MSDS a hard precondition for an order — no
                  supplier below can be ordered from until it is.{' '}
                  <Link to="/msds-registry">Open the MSDS registry →</Link>
                </div>
              </div>
            )}
          </div>
        );
      })}
    </section>
  );
}
