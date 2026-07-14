/**
 * Mirrors the C# records in src/Smx.Domain/Records and src/Smx.Backend/Api.
 *
 * Serialization contract (src/Smx.Domain/Json.cs + Smx.Backend/Program.cs):
 * camelCase properties, enums as strings, nulls omitted when writing.
 */

/** VerdictStatus — src/Smx.Domain/Records/VerdictDoc.cs. Declaration order IS severity order. */
export const VERDICT_SEVERITY = ['Pass', 'Conditional', 'NeedsReview', 'Fail'] as const;
export type VerdictStatus = (typeof VERDICT_SEVERITY)[number];

/** VerdictDimension — src/Smx.Domain/Records/VerdictDoc.cs */
export const VERDICT_DIMENSIONS = [
  'Compatibility',
  'ElementGate',
  'ApplicationCheck',
  'Hazard',
] as const;
export type VerdictDimension = (typeof VERDICT_DIMENSIONS)[number];

/** StageState.Status — src/Smx.Domain/Records/ProjectDoc.cs */
export type StageStatus = 'pending' | 'running' | 'failed' | 'needs-review' | 'done';

/** Stage keys the backend actually tracks — src/Smx.Domain/Records/RecordIds.cs */
export const BACKED_STAGES = ['intake', 'screening', 'matrix'] as const;
export type BackedStage = (typeof BACKED_STAGES)[number];

/** ComponentSpec — src/Smx.Domain/Records/ConstraintsDoc.cs */
export interface ComponentSpec {
  id: string;
  material: string;
  application: string;
  markets: string[];
  objective: string;
}

/** SubstanceSpec — src/Smx.Domain/Records/ConstraintsDoc.cs */
export interface SubstanceSpec {
  element: string;
  form: string;
  cas: string;
}

/** Citation — src/Smx.Domain/Records/ConstraintsDoc.cs */
export interface Citation {
  source: string;
  reference: string;
  retrievedAt: string;
  snippet?: string;
}

/** DimensionVerdict — src/Smx.Domain/Records/VerdictDoc.cs */
export interface DimensionVerdict {
  dimension: VerdictDimension;
  status: VerdictStatus;
  citations: Citation[];
  confidence: number;
  rationale: string;
}

/**
 * Determinations — src/Smx.Domain/Records/VerdictDoc.cs. The ruling on a substance × component.
 * The backend's determination endpoint 422s anything that is not exactly one of these two, so the
 * string that decides whether a chemical enters a customer's product is always one of them.
 */
export const DETERMINATIONS = ['recommended', 'rejected'] as const;
export type Determination = (typeof DETERMINATIONS)[number];

/**
 * MatrixCell — src/Smx.Domain/Records/MatrixDoc.cs
 *
 * FOUR review fields, and the split between them is the design, not bookkeeping:
 *
 *   proposed*      — the AGENT's proposal. It exists so the operator CONFIRMS rather than authors.
 *                    It is not a determination and carries no weight; nothing downstream reads it.
 *   determination* — the OPERATOR's signature. The only field CompliantSet reads, and the only one
 *                    that lets a chemical into a customer's product.
 *
 * They render beside each other and must NEVER render as one field: a UI that collapses them is the
 * agent signing the regulatory gate, which is the single thing Law 9 exists to prevent. Read them
 * through src/domain/proposal.ts, which refuses to let the proposal stand in for the signature.
 *
 * Nulls are omitted on the wire (Smx.Domain/Json.cs), hence the optionals; `evidenceReviewed` is a
 * non-nullable bool server-side and so always arrives.
 */
export interface MatrixCell {
  cas: string;
  componentId: string;
  overall: VerdictStatus;
  dimensions: DimensionVerdict[];
  proposedDetermination?: Determination;
  proposedReason?: string;
  determination?: Determination;
  determinationReason?: string;
  evidenceReviewed?: boolean;
}

/** MatrixDoc — src/Smx.Domain/Records/MatrixDoc.cs */
export interface MatrixDoc {
  id: string;
  projectId: string;
  type: string;
  rows: SubstanceSpec[]; // substances
  columns: string[]; // component ids
  cells: MatrixCell[];
  generatedAt: string;
}

/** StageState — src/Smx.Domain/Records/ProjectDoc.cs */
export interface StageState {
  status: StageStatus;
  attempts: number;
  error?: string | null;
}

/**
 * The projection returned by GET /projects/{projectId}, NOT the whole ProjectDoc.
 * See src/Smx.Backend/Api/ProjectEndpoints.cs:24 — payload and createdAt are dropped.
 */
export interface ProjectSummary {
  projectId: string;
  client: string;
  product: string;
  stages: Record<string, StageState>;
}

/** CreateProjectRequest — src/Smx.Backend/Api/CreateProjectRequest.cs */
export interface CreateProjectRequest {
  client: string;
  product: string;
  components: ComponentSpec[];
  substances: SubstanceSpec[];
  clientRestrictedList?: string[];
}

/** The 202 body of POST /projects. */
export interface CreateProjectResponse {
  projectId: string;
}

/* ---------------------------------------------------------------------------
   The cross-project knowledge layer.

   These three surfaces rendered fixture data behind a MockBadge, on the stated grounds
   that the backend had no endpoint for them. That has not been true for some time:
   `KnowledgeEndpoints.cs` serves GET /marker-library, /learned-conclusions and
   /msds-registry — each taking a `?search=` parameter — plus POST /msds-registry/{cas}/review.

   Mirrors the C# records in src/Smx.Domain/Records/.
   --------------------------------------------------------------------------- */

/** MarkerLibraryDoc — an approved final code, reusable across projects. */
export interface MarkerComposition {
  markers: string[];
  ppm: number;
  ratio: string;
}
export interface ValidatedFor {
  application: string;
  material: string;
  objective: string;
}
export interface MarkerLibraryEntry {
  id: string;
  composition: MarkerComposition;
  validatedFor: ValidatedFor;
  sourceProject: string;
  status: string;
  reuseCount: number;
  createdAt: string;
}

/** LearnedConclusionDoc — one accumulated finding, with provenance and confidence. */
export interface ConclusionScope {
  element?: string | null;
  form?: string | null;
  material?: string | null;
  application?: string | null;
  market?: string | null;
  substance?: string | null;
}
export interface ConclusionProvenance {
  sourceProjects: string[];
  decisions: string[];
}
export interface LearnedConclusion {
  id: string;
  kind: string;
  scope: ConclusionScope;
  finding: string;
  confidence: number;
  provenance: ConclusionProvenance;
  supersedes?: string | null;
  createdAt: string;
}

/** MsdsRegistryDoc — the governance layer that gates procurement. */
export interface MsdsEntry {
  id: string;
  cas: string;
  supplier: string;
  version: string;
  date: string;
  reviewStatus: string;
  reviewedAt?: string | null;
  linkedProjects: string[];
}
