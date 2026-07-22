import type { ProvenanceField } from '../api/types';

/**
 * Where a document came from, rendered in the order the backend chose — SDS and regulatory
 * provenance genuinely differ in shape, so the rail renders what it is handed rather than
 * imposing a schema.
 *
 * "not recorded" is displayed, never hidden. It means the sidecar did not carry that field,
 * which is different from the field not applying, and in an audit surface that difference is
 * the whole point.
 */
export function ProvenanceRail({ fields }: { fields: ProvenanceField[] }) {
  if (fields.length === 0) {
    return (
      <aside className="prov-rail">
        <p className="muted">No provenance recorded for this document.</p>
      </aside>
    );
  }

  return (
    <aside className="prov-rail">
      {fields.map((f) => (
        <div key={f.label} className="prov-field">
          <div className="prov-label" data-testid="provenance-label">
            {f.label}
          </div>
          <div className={f.kind === 'hash' ? 'prov-value prov-hash' : 'prov-value'}>
            {/* The value is checked, not just the kind. A url field can carry "not recorded"
                (RegDocumentProvider emits exactly that for a sidecar with no source URL), and
                linking it would resolve against OUR origin — a link that claims a provenance
                the document does not have. */}
            {f.kind === 'url' && f.value.startsWith('http') ? (
              <a href={f.value} target="_blank" rel="noopener noreferrer">
                {f.value}
              </a>
            ) : (
              f.value
            )}
          </div>
        </div>
      ))}
    </aside>
  );
}
