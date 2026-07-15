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

/** MatrixCell — src/Smx.Domain/Records/MatrixDoc.cs */
export interface MatrixCell {
  cas: string;
  componentId: string;
  overall: VerdictStatus;
  dimensions: DimensionVerdict[];
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
