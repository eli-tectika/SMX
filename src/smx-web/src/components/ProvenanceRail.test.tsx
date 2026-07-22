import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { ProvenanceField } from '../api/types';
import { ProvenanceRail } from './ProvenanceRail';

const FIELDS: ProvenanceField[] = [
  { label: 'Source URL', value: 'https://echa.europa.eu/candidate-list', kind: 'url' },
  { label: 'Authority', value: 'ECHA', kind: 'text' },
  { label: 'SHA-256', value: '9f2c1ae4b8d071', kind: 'hash' },
  { label: 'Fetched', value: 'not recorded', kind: 'text' },
  { label: 'Seed URL', value: 'not recorded', kind: 'url' },
];

describe('ProvenanceRail', () => {
  it('renders every field in the order given', () => {
    render(<ProvenanceRail fields={FIELDS} />);
    const labels = screen.getAllByTestId('provenance-label').map((n) => n.textContent);
    expect(labels).toEqual(['Source URL', 'Authority', 'SHA-256', 'Fetched', 'Seed URL']);
  });

  it('links a url field and leaves the rest as text', () => {
    render(<ProvenanceRail fields={FIELDS} />);
    const link = screen.getByRole('link', { name: /candidate-list/ });
    expect(link).toHaveAttribute('href', 'https://echa.europa.eu/candidate-list');
    // The source is outside our trust boundary; never send a referrer, never let it reach opener.
    expect(link).toHaveAttribute('rel', expect.stringContaining('noopener'));
    expect(link).toHaveAttribute('rel', expect.stringContaining('noreferrer'));
    expect(screen.queryByRole('link', { name: 'ECHA' })).toBeNull();
  });

  /**
   * Spec §3 invariant 6. "not recorded" is a real answer, not a missing value — it means the
   * sidecar did not carry the field. Hiding it would make an absent provenance field
   * indistinguishable from a field that was never part of this document's shape.
   */
  it('shows "not recorded" rather than hiding the field', () => {
    render(<ProvenanceRail fields={FIELDS} />);
    expect(screen.getAllByText('not recorded')).toHaveLength(2);
  });

  /**
   * A url field whose value is "not recorded" is not hypothetical — RegDocumentProvider emits
   * exactly that when the sidecar carries no source URL. The `kind` describes the field, not
   * the value in it, so rendering on kind alone would produce <a href="not recorded">: a link
   * that resolves against our own origin and silently claims a provenance we do not have.
   */
  it('renders an unrecorded url as text, not as a link to nowhere', () => {
    render(<ProvenanceRail fields={FIELDS} />);
    expect(screen.queryByRole('link', { name: 'not recorded' })).toBeNull();
    expect(screen.getAllByRole('link')).toHaveLength(1);
  });

  it('renders nothing but a note when there is no provenance at all', () => {
    render(<ProvenanceRail fields={[]} />);
    expect(screen.getByText(/no provenance recorded/i)).toBeInTheDocument();
  });
});
