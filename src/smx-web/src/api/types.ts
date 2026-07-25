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

/** StageState.Status — src/Smx.Domain/Records/ProjectDoc.cs (StageStatus). */
export type StageStatus =
  | 'pending'
  | 'running'
  | 'failed'
  | 'needs-review'
  | 'done'
  /**
   * Intake only. The interview agent created this project and wrote its dossier, but NO agent has run
   * and none will until the operator presses Start Processing. It is the line between "the agent
   * created something" and "the analysis is running".
   */
  | 'awaiting-confirmation';

/** Stage keys the backend actually tracks — src/Smx.Domain/Records/RecordIds.cs (Stages.All). */
export const BACKED_STAGES = ['intake', 'discovery', 'regulatory', 'matrix'] as const;
export type BackedStage = (typeof BACKED_STAGES)[number];

/** ComponentSpec — src/Smx.Domain/Records/ConstraintsDoc.cs */
export interface ComponentSpec {
  id: string;
  material: string;
  application: string;
  markets: string[];
  objective: string;
  /**
   * Batch MASS in kilograms (never volume). Optional at intake — it is one of the few
   * fields the intake chat is allowed to gap-fill later (IntakeAnswers writable allowlist),
   * so the create form does not collect it.
   */
  batchMassKg?: number;
}

/** SubstanceSpec — src/Smx.Domain/Records/ConstraintsDoc.cs (still used by MatrixDoc.rows). */
export interface SubstanceSpec {
  element: string;
  form: string;
  cas: string;
}

/**
 * ElementPool — src/Smx.Domain/Records/ConstraintsDoc.cs.
 *
 * One element on one component, after the physicist's XRF background has been interpreted. The pool
 * is the list of elements that are USABLE as markers:
 *   V — not detected in the background, so a marker on it can be read.
 *   L — a weak or interfered signal: conditional, and MUST carry a signal-character note (the backend
 *       rejects an L with a blank note — anti-rubber-stamping).
 * An element that IS present in the background is "X" and is deliberately absent from the pool; its
 * measurement is still recorded as a MeasuredBackground, so "measured and rejected" stays
 * distinguishable from "never measured".
 *
 * This is measured data. It cannot be edited through chat (IntakeAnswers refuses it by name) — it is
 * entered on the Background stage and re-entered if the physicist re-measures.
 */
export interface ElementPool {
  component: string;
  element: string;
  line: string;
  status: 'V' | 'L';
  signalNote?: string;
}

/** MeasuredBackground — src/Smx.Domain/Records/ConstraintsDoc.cs */
export interface MeasuredBackground {
  component: string;
  element: string;
  level: number;
  /** Carried, never assumed — which is why this is not called `levelPpm`. */
  unit: string;
}

/** DeviceLod / XrfDevice — src/Smx.Domain/Records/ConstraintsDoc.cs */
export interface DeviceLod {
  element: string;
  lod: number;
  unit: string;
}

export interface XrfDevice {
  model: string;
  lods: DeviceLod[];
}

/**
 * XrfProposal — src/Smx.Domain/Xrf/XrfProposal.cs.
 *
 * One row of the physicist's result. The SAME shape whether it was parsed from a file or typed into
 * the manual grid, because both go through the same server-side validator — the grid is a fallback for
 * unparseable files, not a way around the checks.
 */
export interface XrfProposal {
  rowNumber: number;
  component: string;
  element: string;
  line: string;
  /** 'V' | 'L' | 'X' — X is recorded but is not a pool entry. */
  status: string;
  signalNote?: string | null;
  backgroundLevel?: number | null;
  backgroundUnit?: string | null;
  deviceModel?: string | null;
  deviceLod?: number | null;
  deviceLodUnit?: string | null;
  /** Per-row, from the parser. A row with any problem cannot be confirmed. */
  problems: string[];
}

export interface XrfParseResult {
  proposals: XrfProposal[];
  sheetProblems: string[];
}

/** GET /projects/{id}/xrf — what has already been confirmed. */
export interface XrfState {
  components: string[];
  elementPools: ElementPool[];
  measuredBackgrounds: MeasuredBackground[];
  device?: XrfDevice | null;
}

export interface XrfConfirmed {
  projectId: string;
  pools: number;
  backgrounds: number;
  device?: string | null;
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

/**
 * CreateProjectRequest — src/Smx.Backend/Api/CreateProjectRequest.cs.
 *
 * Production mode: at least one `elementPools` entry (the physicist's XRF background). The
 * backend also accepts a known-candidate mode (`candidates`) plus optional `measuredBackground`
 * and `device` (XRF LODs), but those are eval / awaiting-physics seams and the operator form
 * does not send them — see the plan's deferred section.
 */
export interface CreateProjectRequest {
  client: string;
  product: string;
  components: ComponentSpec[];
  elementPools: ElementPool[];
  clientRestrictedList?: string[];
}

/** The 202 body of POST /projects. */
export interface CreateProjectResponse {
  projectId: string;
}

/* ---------------------------------------------------------------------------
   The WRITE side — spec §4.4 (regulatory), §3 (the agent conversation), §1.5 (revise).

   Every endpoint here was fully implemented on the backend while the frontend only read.
   Mirrors: ProjectEndpoints.cs (regulatory), ChatEndpoints.cs, RevisionEndpoints.cs, and the
   docs in src/Smx.Domain/Records/{VerdictDoc,ChatDocs,RevisionDoc}.cs.
   --------------------------------------------------------------------------- */

/** POST /projects/{id}/regulatory/determination — the operator's signature on one cell. */
export interface DeterminationRequest {
  cas: string;
  componentId: string;
  /** Exactly one of DETERMINATIONS; the backend 422s anything else. */
  determination: Determination;
  /** Mandatory — the backend 422s a blank reason, for a confirm as much as an override. */
  reason: string;
}

/** POST /projects/{id}/regulatory/review — "I have read the evidence", short of a ruling. */
export interface ReviewRequest {
  cas: string;
  componentId: string;
}

/**
 * GET /projects/{id}/gate/regulatory — ProjectEndpoints.cs projects this, it is not a raw GateDoc.
 *
 * `armable` is computed server-side (RegulatoryGate.Armable): true only when every LIVE non-Pass cell
 * has had its evidence reviewed. The sign button reads THIS, never a browser-side tally. Each blocker is
 * the string "unreviewed: {cas}|{componentId} ({Overall})" — parse it, never display it raw.
 * `approvedAt` is omitted (not null) until the gate is signed.
 */
export interface RegulatoryGate {
  status: 'locked' | 'approved';
  armable: boolean;
  blockers: string[];
  approvedAt?: string;
}

/** ChatToolCall — ChatDocs.cs. `recordId` is set when the call WROTE something: the audit link. */
export interface ChatToolCall {
  tool: string;
  summary: string;
  recordId?: string;
}

/**
 * ChatTurn — ChatDocs.cs. One side of a per-(project, stage) thread, oldest-first.
 * An agent turn always carries status "answered" and no error; an operator turn carries the message's
 * own status ("pending" until the agent replies, "failed" with an error if the turn died).
 */
export interface ChatTurn {
  id: string;
  role: 'operator' | 'agent';
  text: string;
  createdAt: string;
  toolCalls: ChatToolCall[];
  status: 'pending' | 'answered' | 'failed';
  error?: string;
}

/** The 202 body of POST …/chat and POST …/revise. */
export interface AcceptedWrite {
  status: 'pending';
}
export interface ChatAccepted extends AcceptedWrite {
  messageId: string;
}
export interface ReviseAccepted extends AcceptedWrite {
  revisionId: string;
}

/**
 * ReviseRequest — RevisionEndpoints.cs. "No direct edits — tell the agent WHY." Only discovery and
 * regulatory are revisable; a regulatory revision must name the verdict's cas + componentId.
 */
export interface ReviseRequest {
  target: string;
  reason: string;
  cas?: string;
  componentId?: string;
}

/** RevisionDoc — src/Smx.Domain/Records/RevisionDoc.cs. The audit ledger of why things changed. */
export interface RevisionDoc {
  id: string;
  projectId: string;
  stage: string;
  target: string;
  reason: string;
  cas?: string;
  componentId?: string;
  status: 'pending' | 'applied' | 'failed';
  error?: string;
  conclusionId?: string;
  createdAt: string;
  appliedAt?: string;
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

/**
 * MsdsRegistryRow — the governance layer that gates procurement, composed at read time over the
 * SDS corpus (`KnowledgeEndpoints.cs`). Everything but `documentId` is the stored governance doc.
 */
export interface MsdsEntry {
  id: string;
  cas: string;
  supplier: string;
  version: string;
  date: string;
  reviewStatus: string;
  reviewedAt?: string | null;
  linkedProjects: string[];
  /**
   * The `sds` document id of the corpus sheet this row was composed from — served, never derived
   * here. Absent for a governance-only row (manual/legacy), which has no sheet behind it and so
   * nothing to open.
   */
  documentId?: string | null;
}

/**
 * What signing an MSDS review answers with: a receipt, not a row.
 *
 * `POST /msds-registry/{cas}/review` returns only what the signature changed. Typing it as a
 * whole MsdsEntry invited callers to substitute it for one, which silently dropped the supplier,
 * the revision date and the sheet link from a row that had just been signed.
 */
export interface MsdsReviewReceipt {
  cas: string;
  reviewStatus: string;
  reviewedAt?: string | null;
}

/* ---------------------------------------------------------------------------
   Documents — the file viewer's contract (design 2026-07-22).

   `kind` is the FACET: a safety sheet the system never obtained still reports
   'sds', because it is a missing sheet and not a fourth category. Only the id
   distinguishes it, and only because it resolves against a different container.
   --------------------------------------------------------------------------- */

export type DocumentKind = 'sds' | 'reg' | 'seed';
export type DocumentState = 'available' | 'missing' | 'superseded';

export interface DocumentSummary {
  id: string;
  kind: DocumentKind;
  title: string;
  subtitle: string;
  available: boolean;
  state: DocumentState;
  contentType: string | null;
  officialDate: string | null;
  ingestedUtc: string | null;
}

export interface ProvenanceField {
  label: string;
  value: string;
  kind: 'text' | 'url' | 'hash';
}

export interface DocumentDetail {
  summary: DocumentSummary;
  provenance: ProvenanceField[];
  unavailableReason: string | null;
  unavailableDetail: string | null;
  supersededById: string | null;
}

/** A chunk exactly as indexed — never re-extracted or cleaned up on the way here. */
export interface DocumentChunk {
  ordinal: number;
  text: string;
  entryId: string | null;
  section: string | null;
}

export interface DocumentBytes {
  blob: Blob;
  contentType: string;
}

/* ---------------------------------------------------------------------------
   The pre-project interview — "New project" as a conversation rather than a form.

   Mirrors src/Smx.Domain/Intake/DossierEntry.cs, src/Smx.Domain/Intake/IntakeQuestions.cs and
   src/Smx.Domain/Records/IntakeDocs.cs.
   --------------------------------------------------------------------------- */

/** DossierState — src/Smx.Domain/Intake/DossierEntry.cs. There is deliberately no "never asked". */
export type DossierState = 'answered' | 'agent-proposed' | 'unknown' | 'not-applicable';

/** DossierEntry — src/Smx.Domain/Intake/DossierEntry.cs */
export interface DossierEntry {
  questionId: string;
  state: DossierState;
  answer: string;
  provenance: string;
  confidence?: string;
  recordedAt: string;
}

/** IntakeQuestion — src/Smx.Domain/Intake/IntakeQuestions.cs, served by GET /intake-questions. */
export interface IntakeQuestion {
  id: string;
  prompt: string;
  why: string;
}

/** AttachmentStatus — src/Smx.Domain/Records/IntakeDocs.cs */
export type AttachmentStatus = 'extracted' | 'unsupported' | 'failed';

/** SessionAttachment — src/Smx.Domain/Records/IntakeDocs.cs */
export interface SessionAttachment {
  fileId: string;
  filename: string;
  contentType: string;
  sizeBytes: number;
  blobPath: string;
  textBlobPath?: string;
  status: AttachmentStatus;
  error?: string;
}

/** InterviewTurn — src/Smx.Domain/Records/IntakeDocs.cs */
export interface InterviewTurn {
  role: 'operator' | 'agent';
  text: string;
  toolCalls: string[];
  createdAt: string;
}

/** IntakeSessionDoc — src/Smx.Domain/Records/IntakeDocs.cs */
export interface IntakeSession {
  sessionId: string;
  status: 'interviewing' | 'created' | 'abandoned';
  client: string;
  product: string;
  summary: string;
  turns: InterviewTurn[];
  attachments: SessionAttachment[];
  dossier: DossierEntry[];
  proposedComponents: ComponentSpec[];
  createdProjectId?: string;
  createdAt: string;
  updatedAt: string;
}

/** IntakeBriefDoc — src/Smx.Domain/Records/IntakeDocs.cs */
export interface IntakeBrief {
  projectId: string;
  sessionId: string;
  summary: string;
  dossier: DossierEntry[];
  components: ComponentSpec[];
  attachments: SessionAttachment[];
  transcript: InterviewTurn[];
  createdAt: string;
}
