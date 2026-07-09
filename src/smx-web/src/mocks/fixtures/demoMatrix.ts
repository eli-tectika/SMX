import type { Citation, DimensionVerdict, MatrixCell, MatrixDoc, VerdictStatus } from '../../api/types';
import { fold } from '../../domain/matrix';

/**
 * The demo matrix is TypeScript rather than JSON so it is checked against the real
 * MatrixDoc type: if the C# record changes shape, this fixture stops compiling
 * instead of quietly serving a doc the app can no longer read.
 */

const cite = (source: string, reference: string, snippet?: string): Citation => ({
  source,
  reference,
  retrievedAt: '2026-07-01T00:00:00Z',
  snippet,
});

const dim = (
  dimension: DimensionVerdict['dimension'],
  status: VerdictStatus,
  confidence: number,
  rationale: string,
  citations: Citation[],
): DimensionVerdict => ({ dimension, status, citations, confidence, rationale });

const SUBSTANCES = [
  { element: 'Y', form: '2-ethylhexanoate', cas: '14832-90-7' },
  { element: 'Gd', form: 'neodecanoate', cas: '68583-78-8' },
  { element: 'Ba', form: '2-ethylhexanoate', cas: '2457-01-4' },
  { element: 'Pb', form: 'naphthenate', cas: '61790-14-5' },
];

const COMPONENTS = ['bottle', 'sticker', 'lid', 'liquid'];

/** Per-substance dimension verdicts. ElementGate is product-wide; the rest vary by component. */
function dimensionsFor(cas: string, componentId: string): DimensionVerdict[] {
  const clp = cite('ECHA C&L', cas);
  const annexII = cite('EC 1223/2009', 'Annex II');

  switch (cas) {
    case '14832-90-7': // Y — clean everywhere
      return [
        dim('ElementGate', 'Pass', 0.97, 'Yttrium is not listed in Annex II.', [annexII]),
        dim('ApplicationCheck', 'Pass', 0.93, 'No restriction for leave-on cosmetics in EU or US.', [
          cite('EC 1223/2009', 'Annex III'),
        ]),
        dim('Compatibility', 'Pass', 0.9, `No Y background detected on the ${componentId}.`, [
          cite('xrf-run', `2026-06-28/${componentId}`),
        ]),
        dim('Hazard', 'Pass', 0.95, 'Not classified under CLP.', [clp]),
      ];

    case '68583-78-8': // Gd — conditional on the lid
      return [
        dim('ElementGate', 'Pass', 0.96, 'Gadolinium is not listed in Annex II.', [annexII]),
        dim(
          'ApplicationCheck',
          componentId === 'lid' ? 'Conditional' : 'Pass',
          0.88,
          componentId === 'lid'
            ? 'Permitted below 0.5% w/w for leave-on contact surfaces.'
            : 'No restriction for this application.',
          [cite('EC 1223/2009', 'Annex III #142')],
        ),
        dim('Compatibility', 'Pass', 0.86, `No Gd background on the ${componentId}.`, [
          cite('xrf-run', `2026-06-28/${componentId}`),
        ]),
        dim('Hazard', 'Conditional', 0.82, 'Skin Irrit. 2 — handling controls required.', [clp]),
      ];

    case '2457-01-4': // Ba — parked on the R.E.
      return [
        dim(
          'ElementGate',
          'NeedsReview',
          0.55,
          'Soluble barium salts are listed; the solubility class of this salt is unresolved.',
          [cite('EC 1223/2009', 'Annex II #189')],
        ),
        dim('ApplicationCheck', 'NeedsReview', 0.5, 'Depends on the solubility classification.', [
          cite('EC 1223/2009', 'Annex II #189'),
        ]),
        dim(
          'Compatibility',
          componentId === 'sticker' ? 'Fail' : 'Conditional',
          0.79,
          componentId === 'sticker'
            ? 'Ba is present in the sticker background.'
            : `Weak Ba signal on the ${componentId}.`,
          [cite('xrf-run', `2026-06-28/${componentId}`)],
        ),
        dim('Hazard', 'Conditional', 0.84, 'Acute Tox. 4 — handling controls required.', [clp]),
      ];

    default: // Pb — barred product-wide by the element gate
      return [
        dim('ElementGate', 'Fail', 0.99, 'Lead and its compounds are prohibited.', [
          cite('EC 1223/2009', 'Annex II #289', 'Lead and its compounds'),
        ]),
        dim('ApplicationCheck', 'Fail', 0.99, 'Barred by the product-wide element gate.', [
          cite('EC 1223/2009', 'Annex II #289'),
        ]),
        dim('Compatibility', 'Fail', 0.91, `Pb is present in the ${componentId} background.`, [
          cite('xrf-run', `2026-06-28/${componentId}`),
        ]),
        dim('Hazard', 'Fail', 0.98, 'Repr. 1A, STOT RE 2.', [clp]),
      ];
  }
}

const cells: MatrixCell[] = SUBSTANCES.flatMap((s) =>
  COMPONENTS.map((componentId) => {
    const dimensions = dimensionsFor(s.cas, componentId);
    // Fold rather than hand-write `overall`, so the fixture can never contradict itself.
    return { cas: s.cas, componentId, overall: fold(dimensions), dimensions };
  }),
);

export const demoMatrix: MatrixDoc = {
  id: 'proj-demo|matrix',
  projectId: 'proj-demo',
  type: 'matrix',
  rows: SUBSTANCES,
  columns: COMPONENTS,
  cells,
  generatedAt: '2026-07-08T09:14:00Z',
};
