import { MockBadge } from '../../components/MockBadge';
import cost from '../../mocks/fixtures/cost.json';

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

/** Cost & availability (spec §4.6) — the per-molecule supplier audit. */
export function Cost() {
  const { molecules } = cost as { molecules: Molecule[] };

  return (
    <section className="screen">
      <div className="cap">
        <b>Cost &amp; availability</b> &nbsp;·&nbsp; spec §4.6 — supplier audit per molecule
      </div>

      <MockBadge note="No supplier catalog was queried. Prices, lead times and availability are illustrative." />

      {molecules.map((m) => (
        <div key={m.element + m.form} style={{ marginBottom: 16 }}>
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, marginBottom: 6 }}>
            <span style={{ fontSize: 13, fontWeight: 500 }}>
              {m.element} {m.form}
            </span>
            <span className="tiny muted">{m.grade}</span>
            {m.supplyRisk && (
              <span className="tiny" style={{ color: 'var(--text-warning)', marginLeft: 'auto' }}>
                <i className="ti ti-alert-triangle" aria-hidden="true" /> supply risk
              </span>
            )}
          </div>

          <table className="mx">
            <thead>
              <tr>
                <th>Supplier</th>
                <th style={{ width: 120 }}>Price / kg</th>
                <th style={{ width: 110 }}>Lead time</th>
                <th style={{ width: 140 }}>Availability</th>
              </tr>
            </thead>
            <tbody>
              {m.suppliers.map((s) => (
                <tr key={s.name}>
                  <td>{s.name}</td>
                  <td>
                    {s.currency} {s.pricePerKg.toLocaleString()}
                  </td>
                  <td className="secondary">{s.leadTimeDays} days</td>
                  <td>
                    <span className={`chip ${s.availability === 'In stock' ? 'v' : 'l'}`}>
                      {s.availability}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          {m.supplyRisk && (
            <div className="banner warn" style={{ margin: '8px 0 0' }}>
              <i className="ti ti-alert-triangle" aria-hidden="true" />
              <div>{m.supplyRisk}</div>
            </div>
          )}
        </div>
      ))}
    </section>
  );
}
