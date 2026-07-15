import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { createProject } from '../api/client';
import type { ComponentSpec, SubstanceSpec } from '../api/types';
import { rememberProject } from '../hooks/useRecentProjects';

const blankComponent = (): ComponentSpec => ({
  id: '',
  material: '',
  application: '',
  markets: [],
  objective: '',
});
const blankSubstance = (): SubstanceSpec => ({ element: '', form: '', cas: '' });

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
  substances: SubstanceSpec[],
): string | null {
  if (!client.trim() || !product.trim()) return 'client and product are required';
  if (components.length === 0) return 'at least one component is required';
  if (substances.length === 0) return 'at least one candidate substance is required';
  if (components.some((c) => !c.id.trim())) return 'every component needs an id';
  if (substances.some((s) => !s.cas.trim())) return 'every substance needs a CAS number';
  if (new Set(components.map((c) => c.id)).size !== components.length)
    return 'component ids must be unique';
  if (new Set(substances.map((s) => s.cas)).size !== substances.length)
    return 'substance CAS numbers must be unique';
  return null;
}

export function NewProject() {
  const navigate = useNavigate();
  const [client, setClient] = useState('');
  const [product, setProduct] = useState('');
  const [restricted, setRestricted] = useState('');
  const [components, setComponents] = useState<ComponentSpec[]>([blankComponent()]);
  const [substances, setSubstances] = useState<SubstanceSpec[]>([blankSubstance()]);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const patchComponent = (i: number, patch: Partial<ComponentSpec>) =>
    setComponents((cs) => cs.map((c, j) => (i === j ? { ...c, ...patch } : c)));
  const patchSubstance = (i: number, patch: Partial<SubstanceSpec>) =>
    setSubstances((ss) => ss.map((s, j) => (i === j ? { ...s, ...patch } : s)));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const invalid = validate(client, product, components, substances);
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
        substances,
        clientRestrictedList: restricted
          .split(',')
          .map((s) => s.trim())
          .filter(Boolean),
      });
      rememberProject({
        projectId,
        client: client.trim(),
        product: product.trim(),
        createdAt: new Date().toISOString(),
      });
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
        spec §4.1 — this is the only screen that writes to
        the record
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
        title="Candidate substances"
        hint="Screened per component against the element gate, application check and hazard layer."
        onAdd={() => setSubstances((ss) => [...ss, blankSubstance()])}
      />
      <table className="mx" style={{ marginBottom: 18 }}>
        <thead>
          <tr>
            <th style={{ width: 110 }}>Element</th>
            <th>Form</th>
            <th style={{ width: 180 }}>CAS</th>
            <th style={{ width: 34 }} />
          </tr>
        </thead>
        <tbody>
          {substances.map((s, i) => (
            <tr key={i}>
              <td>
                <input
                  type="text"
                  value={s.element}
                  placeholder="Zr"
                  onChange={(e) => patchSubstance(i, { element: e.target.value })}
                  aria-label={`Substance ${i + 1} element`}
                />
              </td>
              <td>
                <input
                  type="text"
                  value={s.form}
                  placeholder="neodecanoate"
                  onChange={(e) => patchSubstance(i, { form: e.target.value })}
                  aria-label={`Substance ${i + 1} form`}
                />
              </td>
              <td>
                <input
                  type="text"
                  value={s.cas}
                  placeholder="39049-04-2"
                  onChange={(e) => patchSubstance(i, { cas: e.target.value })}
                  aria-label={`Substance ${i + 1} CAS`}
                />
              </td>
              <td>
                <RemoveButton
                  disabled={substances.length === 1}
                  onClick={() => setSubstances((ss) => ss.filter((_, j) => j !== i))}
                  label={`Remove substance ${i + 1}`}
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
