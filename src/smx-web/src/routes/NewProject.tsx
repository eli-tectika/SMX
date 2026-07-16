import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { createProject } from '../api/client';
import type { ComponentSpec, ElementPool } from '../api/types';

const blankComponent = (): ComponentSpec => ({
  id: '',
  material: '',
  application: '',
  markets: [],
  objective: '',
});
// "Kα" matches the backend test data; the operator adjusts the line if the physicist read another.
const blankPool = (component: string): ElementPool => ({
  component,
  element: '',
  line: 'Kα',
  status: 'V',
});

/**
 * Client-side validation mirrors CreateProjectRequest.Validate() so the operator
 * gets feedback before a round trip. It is a convenience, not the contract — the
 * server's 400 body is still surfaced verbatim, because the server is the authority
 * on what a valid project is.
 */
function validate(
  client: string,
  product: string,
  components: ComponentSpec[],
  pools: ElementPool[],
): string | null {
  if (!client.trim() || !product.trim()) return 'client and product are required';
  if (components.length === 0) return 'at least one component is required';
  if (components.some((c) => !c.id.trim())) return 'every component needs an id';
  if (new Set(components.map((c) => c.id)).size !== components.length)
    return 'component ids must be unique';
  // Production mode: the physicist's element pools are what the project screens against.
  if (pools.length === 0) return 'at least one element pool is required';
  const ids = new Set(components.map((c) => c.id));
  if (pools.some((p) => !p.component || !ids.has(p.component)))
    return 'every element pool must reference a declared component';
  if (pools.some((p) => !p.element.trim())) return 'every element pool needs an element';
  // The anti-rubber-stamping rule: a conditional (L) reading must carry its signal-character note.
  if (pools.some((p) => p.status === 'L' && !p.signalNote?.trim()))
    return 'each conditional (L) element pool entry must carry a signal-character note';
  return null;
}

export function NewProject() {
  const navigate = useNavigate();
  const [client, setClient] = useState('');
  const [product, setProduct] = useState('');
  const [restricted, setRestricted] = useState('');
  const [components, setComponents] = useState<ComponentSpec[]>([blankComponent()]);
  const [pools, setPools] = useState<ElementPool[]>([blankPool('')]);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const patchComponent = (i: number, patch: Partial<ComponentSpec>) =>
    setComponents((cs) => cs.map((c, j) => (i === j ? { ...c, ...patch } : c)));
  const patchPool = (i: number, patch: Partial<ElementPool>) =>
    setPools((ps) => ps.map((p, j) => (i === j ? { ...p, ...patch } : p)));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const invalid = validate(client, product, components, pools);
    if (invalid) {
      setError(invalid);
      return;
    }
    setSubmitting(true);
    setError(null);
    try {
      const { projectId } = await createProject({
        client: client.trim(),
        product: product.trim(),
        components,
        // Drop the signal note on V rows — it is meaningful only for a conditional reading.
        elementPools: pools.map((p) =>
          p.status === 'L' ? p : { component: p.component, element: p.element, line: p.line, status: p.status },
        ),
        clientRestrictedList: restricted
          .split(',')
          .map((s) => s.trim())
          .filter(Boolean),
      });
      // Nothing to remember: POST wrote the project to the record before it answered, so GET /projects
      // already lists it. The dashboard finds it on any browser, not just this one.
      navigate(`/p/${projectId}/intake`);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      setSubmitting(false);
    }
  }

  return (
    <form className="screen" onSubmit={submit}>
      <div className="cap">
        <b>Intake &amp; scoping</b>
        This is the only screen that writes to the record
      </div>

      {error && (
        <div className="banner danger" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>{error}</div>
        </div>
      )}

      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginBottom: 18 }}>
        <label>
          <div className="tiny muted" style={{ marginBottom: 3 }}>
            Client
          </div>
          <input type="text" value={client} onChange={(e) => setClient(e.target.value)} required />
        </label>
        <label>
          <div className="tiny muted" style={{ marginBottom: 3 }}>
            Product
          </div>
          <input type="text" value={product} onChange={(e) => setProduct(e.target.value)} required />
        </label>
      </div>

      <SectionHeader
        title="Components"
        hint="Background, marker form, ppm and codes run independently per component."
        onAdd={() => setComponents((cs) => [...cs, blankComponent()])}
      />
      <table className="mx" style={{ marginBottom: 18 }}>
        <thead>
          <tr>
            <th style={{ width: 110 }}>Id</th>
            <th>Material</th>
            <th>Application</th>
            <th style={{ width: 150 }}>Markets (comma-sep)</th>
            <th>Objective</th>
            <th style={{ width: 34 }} />
          </tr>
        </thead>
        <tbody>
          {components.map((c, i) => (
            <tr key={i}>
              <td>
                <input
                  type="text"
                  value={c.id}
                  placeholder="bottle"
                  onChange={(e) => patchComponent(i, { id: e.target.value })}
                  aria-label={`Component ${i + 1} id`}
                />
              </td>
              <td>
                <input
                  type="text"
                  value={c.material}
                  placeholder="PET"
                  onChange={(e) => patchComponent(i, { material: e.target.value })}
                  aria-label={`Component ${i + 1} material`}
                />
              </td>
              <td>
                <input
                  type="text"
                  value={c.application}
                  placeholder="leave-on cosmetic"
                  onChange={(e) => patchComponent(i, { application: e.target.value })}
                  aria-label={`Component ${i + 1} application`}
                />
              </td>
              <td>
                <input
                  type="text"
                  value={c.markets.join(', ')}
                  placeholder="EU, US"
                  onChange={(e) =>
                    patchComponent(i, {
                      markets: e.target.value
                        .split(',')
                        .map((m) => m.trim())
                        .filter(Boolean),
                    })
                  }
                  aria-label={`Component ${i + 1} markets`}
                />
              </td>
              <td>
                <input
                  type="text"
                  value={c.objective}
                  placeholder="brand"
                  onChange={(e) => patchComponent(i, { objective: e.target.value })}
                  aria-label={`Component ${i + 1} objective`}
                />
              </td>
              <td>
                <RemoveButton
                  disabled={components.length === 1}
                  onClick={() => setComponents((cs) => cs.filter((_, j) => j !== i))}
                  label={`Remove component ${i + 1}`}
                />
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <SectionHeader
        title="Element pools"
        hint="The physicist's measured XRF background, per component. V = present · L = conditional (needs a signal note)."
        onAdd={() => setPools((ps) => [...ps, blankPool(components[0]?.id ?? '')])}
      />
      <table className="mx" style={{ marginBottom: 18 }}>
        <thead>
          <tr>
            <th style={{ width: 130 }}>Component</th>
            <th style={{ width: 90 }}>Element</th>
            <th style={{ width: 80 }}>Line</th>
            <th style={{ width: 96 }}>Status</th>
            <th>Signal note</th>
            <th style={{ width: 34 }} />
          </tr>
        </thead>
        <tbody>
          {pools.map((p, i) => (
            <tr key={i}>
              <td>
                <select
                  value={p.component}
                  onChange={(e) => patchPool(i, { component: e.target.value })}
                  aria-label={`Pool ${i + 1} component`}
                >
                  <option value="" disabled>
                    choose…
                  </option>
                  {components
                    .filter((c) => c.id.trim())
                    .map((c) => (
                      <option key={c.id} value={c.id}>
                        {c.id}
                      </option>
                    ))}
                </select>
              </td>
              <td>
                <input
                  type="text"
                  value={p.element}
                  placeholder="Zr"
                  onChange={(e) => patchPool(i, { element: e.target.value })}
                  aria-label={`Pool ${i + 1} element`}
                />
              </td>
              <td>
                <input
                  type="text"
                  value={p.line}
                  placeholder="Kα"
                  onChange={(e) => patchPool(i, { line: e.target.value })}
                  aria-label={`Pool ${i + 1} line`}
                />
              </td>
              <td>
                <div className="seg" role="group" aria-label={`Pool ${i + 1} status`}>
                  {(['V', 'L'] as const).map((st) => (
                    <button
                      key={st}
                      type="button"
                      className="seg__btn"
                      onClick={() => patchPool(i, { status: st })}
                      aria-pressed={p.status === st}
                    >
                      {st}
                    </button>
                  ))}
                </div>
              </td>
              <td>
                {p.status === 'L' ? (
                  <input
                    type="text"
                    value={p.signalNote ?? ''}
                    placeholder="e.g. trace, near LOD — why it's conditional"
                    onChange={(e) => patchPool(i, { signalNote: e.target.value })}
                    aria-label={`Pool ${i + 1} signal note`}
                  />
                ) : (
                  <span className="tiny muted">— (only for conditional readings)</span>
                )}
              </td>
              <td>
                <RemoveButton
                  disabled={pools.length === 1}
                  onClick={() => setPools((ps) => ps.filter((_, j) => j !== i))}
                  label={`Remove element pool ${i + 1}`}
                />
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <label style={{ display: 'block', marginBottom: 18 }}>
        <div className="tiny muted" style={{ marginBottom: 3 }}>
          Client restricted list (optional, comma-separated elements)
        </div>
        <input
          type="text"
          value={restricted}
          placeholder="Ni, Co"
          onChange={(e) => setRestricted(e.target.value)}
        />
      </label>

      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <button className="btn primary" type="submit" disabled={submitting}>
          {submitting ? 'Creating…' : 'Create project'}
        </button>
        <span className="tiny muted">
          Writing the record starts the intake agent. This cannot be undone from the UI.
        </span>
      </div>
    </form>
  );
}

function SectionHeader({
  title,
  hint,
  onAdd,
}: {
  title: string;
  hint: string;
  onAdd: () => void;
}) {
  return (
    <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, margin: '0 0 8px' }}>
      <span style={{ fontSize: 13, fontWeight: 500 }}>{title}</span>
      <span className="tiny muted">{hint}</span>
      <button className="btn" type="button" onClick={onAdd} style={{ marginLeft: 'auto' }}>
        <i className="ti ti-plus" aria-hidden="true" /> Add
      </button>
    </div>
  );
}

function RemoveButton({
  disabled,
  onClick,
  label,
}: {
  disabled: boolean;
  onClick: () => void;
  label: string;
}) {
  return (
    <button
      className="btn"
      type="button"
      disabled={disabled}
      onClick={onClick}
      aria-label={label}
      style={{ padding: '4px 7px' }}
    >
      <i className="ti ti-trash" aria-hidden="true" />
    </button>
  );
}
