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

/**
 * StageState.Status — src/Smx.Domain/Records/ProjectDoc.cs + StageDispatcher.
 *
 * The three `awaiting-*` states are the spec's PARK states, and they are real: the dispatcher writes
 * `awaiting-physics` and `awaiting-operator` (StageDispatcher.cs:248,255) and `awaiting-RE` (:185). They are
 * not "pending". `pending` means the agent has not started; an `awaiting-*` means the record is stopped on a
 * named human, and for `awaiting-operator` the stage's `error` string says exactly what to enter.
 */
export type StageStatus =
  | 'pending'
  | 'running'
  | 'failed'
  | 'needs-review'
  | 'done'
  | 'awaiting-RE'
  | 'awaiting-physics'
  | 'awaiting-operator';

/** The park states, and who each one is stopped on. Order = the operator's ability to act. */
export const AWAITING_STATES = ['awaiting-operator', 'awaiting-physics', 'awaiting-RE'] as const;
export type AwaitingStatus = (typeof AWAITING_STATES)[number];
export const isAwaiting = (s: StageStatus): s is AwaitingStatus =>
  (AWAITING_STATES as readonly string[]).includes(s);

/** Stage keys the backend actually tracks — src/Smx.Domain/Records/RecordIds.cs (Stages.All). */
export const BACKED_STAGES = ['intake', 'discovery', 'regulatory', 'matrix', 'dosing', 'cost'] as const;
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
  /**
   * The substrate's physical state — "liquid" | "solid" | "oil-soluble" | "coating" (free text).
   * Drives the pool agent's form-class choice (oil-soluble → organocomplex; solid polymer →
   * oxide/salt; coating → dispersible compound). Collected in the intake form.
   */
  physicalState?: string;
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
 * The physicist's measured XRF background for one element on one component, already
 * interpreted into a status. "V" = present/verified; "L" = conditional, and a conditional
 * pool MUST carry a signal-character note (the backend rejects an L with a blank note).
 * This data is the physicist's and cannot be edited through chat — only re-entered at intake.
 */
export interface ElementPool {
  component: string;
  element: string;
  line: string;
  status: 'V' | 'L';
  signalNote?: string;
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
 * MeasuredBackground — src/Smx.Domain/Records/ConstraintsDoc.cs.
 *
 * The physicist's measured background for one element in one component. `unit` is CARRIED, not assumed,
 * which is why the field is `level` and not `levelPpm` — DetectionFloor refuses to add a background to a
 * LOD whose unit differs, because mixing counts with ppm yields a plausible, wrong number. Render the unit.
 */
export interface MeasuredBackground {
  component: string;
  element: string;
  level: number;
  unit: string;
}

/** DeviceLod / XrfDevice — ConstraintsDoc.cs. The deployment XRF unit whose LODs the ppm floor targets. */
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
 * The intake payload, as held on the project record — the body of POST /projects, gap-filled in place by
 * the intake chat's record_answer while intake is still gathering (ChatTools.cs:227-230).
 *
 * `providedCandidates` is the ONE field here that is a provenance trap. It is the known-candidate mode
 * seam: when non-empty, Discovery is BYPASSED and these become the candidates doc verbatim, never having
 * passed DiscoveryAgent.Validate (StageDispatcher.cs:89-98). They are operator/eval input that carries
 * exactly the authority of an agent-cited candidate — so a UI that renders them beside, or as, Discovery
 * output is fabricating agent provenance. Label them or do not render them.
 *
 * `measuredBackground: []` with no `device` is not an empty screen — it is the exact precondition Dosing
 * parks on (awaiting-physics). It is a FACT about the record, and the honest thing to show.
 */
export interface ProjectPayload {
  components: ComponentSpec[];
  /** Optional: a need-only project carries no operator element pool — the pool agent proposes one instead. */
  elementPools?: ElementPool[];
  providedCandidates: unknown[];
  clientRestrictedList: string[];
  measuredBackground: MeasuredBackground[];
  /** Absent (no key at all) when the operator has no XRF device on file — never a null masquerading as one. */
  device?: XrfDevice;
}

/**
 * The projection returned by GET /projects/{projectId}, NOT the whole ProjectDoc.
 * See src/Smx.Backend/Api/ProjectEndpoints.cs:48 — `createdAt` is still dropped.
 *
 * `payload` is optional here only so existing ProjectSummary fixtures stay valid; the backend always
 * sends it. Treat its absence as "not loaded", never as "the operator submitted nothing".
 */
export interface ProjectSummary {
  projectId: string;
  client: string;
  product: string;
  stages: Record<string, StageState>;
  payload?: ProjectPayload;
}

/**
 * One item of GET /projects — the card projection. See src\Smx.Backend\Api\ProjectEndpoints.cs:52.
 *
 * Deliberately NOT a ProjectSummary with a date bolted on, even though it is structurally assignable to
 * one (which is what lets the dashboard hand these to `bucket` / `whatsBlocking` unchanged). The list
 * carries no `payload` and never will — a card renders none of it — whereas it carries `createdAt`, which
 * the detail route drops. Two projections of one document, each shaped by its screen.
 */
export interface ProjectListItem {
  projectId: string;
  client: string;
  product: string;
  stages: Record<string, StageState>;
  createdAt: string;
}

/**
 * CreateProjectRequest — src/Smx.Backend/Api/CreateProjectRequest.cs.
 *
 * Need-only: the operator submits the need (components incl. each substrate's `physicalState`) and the pool
 * agent proposes the candidate pool. The backend still accepts an explicit `elementPools` (physicist XRF
 * background) and a known-candidate mode (`candidates`) plus `measuredBackground` / `device`, but those are
 * eval / awaiting-physics seams and the operator form does not send them — see the pool subsystem design.
 */
export interface CreateProjectRequest {
  client: string;
  product: string;
  components: ComponentSpec[];
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
 * ReviseRequest — RevisionEndpoints.cs. "No direct edits — tell the agent WHY."
 *
 * Revisable: discovery, regulatory, dosing (RevisionEffects.IsRevisable). NOT matrix (deterministically
 * assembled), NOT cost (a table lookup — there is no "why" to record over a price fetch), NOT intake (the
 * blast radius is the whole project). A regulatory revision must name the verdict's cas + componentId.
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
   DOSING — src/Smx.Domain/Records/DosingDoc.cs.

   NOTE the casing split: VerdictStatus is PascalCase on the wire ("Pass"), because it is a C# enum. Every
   value here is a `const string` and arrives LOWERCASE. They are not the same convention; do not unify them.
   --------------------------------------------------------------------------- */

/**
 * BoundKinds — DosingDoc.cs:8-18. Lowercase.
 *
 * "measured" is the physicist's data and is NEVER produced by the agent: DosingAgent rejects an
 * agent-authored bound claiming it. That asymmetry is the point — an agent that could label its own estimate
 * "measured" would launder a guess into the one field the operator trusts absolutely. The UI renders the two
 * differently for the same reason.
 */
export const BOUND_KINDS = ['measured', 'regulatory', 'estimate'] as const;
export type BoundKind = (typeof BOUND_KINDS)[number];

/**
 * One end of a ppm window, WITH WHERE IT CAME FROM (DosingDoc.cs:28).
 *
 * The two ends are not equally trustworthy: the floor is measured (confidence 1.0), while an upper bound with
 * no regulatory cap is an estimate known to run low. `basis` is free prose, not a structured Citation —
 * Dosing carries no Citation objects at all. Render it as prose; do not build a citation chip for it.
 */
export interface Bound {
  ppm: number;
  basis: string;
  kind: BoundKind;
  confidence: number;
}

/**
 * The dosable range for one substance in one component (DosingDoc.cs:33-35).
 *
 * `recommendedPpm` is a single SCALAR that sits strictly inside (floor.ppm, upper.ppm) — there is no
 * recommended low/high band. A ppm at or below the floor is a marker nobody can read in the field, and
 * nothing downstream catches it.
 */
export interface PpmWindow {
  componentId: string;
  cas: string;
  element: string;
  floor: Bound;
  upper: Bound;
  recommendedPpm: number;
  quantificationPpm: number;
}

/**
 * One marker inside a code (DosingDoc.cs:38-39). Both masses are MILLIGRAMS and they are DIFFERENT numbers.
 *
 * `elementMassMg` is what must end up in the batch; `compoundMassMg` is what you BUY. Rendering the element
 * mass as the order quantity under-doses an oxide by its non-metal fraction.
 */
export interface CodeMarker {
  cas: string;
  element: string;
  ppm: number;
  metalLoading: number;
  elementMassMg: number;
  compoundMassMg: number;
}

/**
 * A code: 2–3 markers in ONE component, identified by their ppm ratio (DosingDoc.cs:43-68).
 *
 * A code has no name and no kind — its identity IS `ratioSignature`, e.g. "Y:Zr = 1.00:0.50". That field is
 * DERIVED server-side on every read and IGNORED on write (STJ drops it inbound), so it is read-only here:
 * never send it back, and never render it to more precision than the 2dp the domain deliberately chose —
 * that precision silently defines the resolution limit of a code's identity.
 */
export interface MarkerCode {
  componentId: string;
  markers: CodeMarker[];
  rationale: string;
  readonly ratioSignature: string;
}

/** DosingDoc — DosingDoc.cs:70-84. One per project; the per-component split lives INSIDE, on each row. */
export interface DosingDoc {
  id: string;
  projectId: string;
  type: string;
  windows: PpmWindow[];
  codes: MarkerCode[];
  /** The SOFT checkpoint (UX §4.5). A review note, NOT a gate — it blocks nothing and must never be made to. */
  reviewNote?: string;
  reviewedAt?: string;
  generatedAt: string;
}

/**
 * POST /projects/{id}/dosing/loading — DosingEndpoints.cs:79.
 *
 * The one number in no catalog: the mass fraction of the marker element in the compound (Y₂O₃ = 0.787). It is
 * written to the CROSS-PROJECT knowledge layer keyed by CAS alone, so the next project never asks again.
 * `metalLoading` must be in (0, 1]; `basis` is required — it is the source that makes the number checkable.
 */
export interface LoadingRequest {
  cas: string;
  element: string;
  form: string;
  metalLoading: number;
  basis: string;
}

/** POST /projects/{id}/dosing/review — DosingEndpoints.cs:82. The note is required. */
export interface DosingReviewRequest {
  note: string;
}

/* ---------------------------------------------------------------------------
   COST — src/Smx.Domain/Records/CostDoc.cs. Read-only: Cost holds no agent and is not revisable.
   --------------------------------------------------------------------------- */

/** A price and the listing it came from (CostDoc.cs:5). Per GRAM. `currency` can only ever be "USD". */
export interface PriceQuote {
  usdPerGram: number;
  currency: string;
  supplier: string;
  pack: string;
  citation: Citation;
}

/**
 * The audit for one substance (CostDoc.cs:11-16).
 *
 * `bestQuote` is ABSENT (nulls are omitted) when nothing parseable was on file, and `priceNote` says so in
 * words. Nothing is interpolated, averaged, or currency-converted into existence — a Cost stage that invented
 * a price would be fabricating the single number procurement acts on. Render the absence, never a zero.
 *
 * `suppliers` is NAMES ONLY — there is no per-supplier price to compare, and no lead time anywhere in Cost.
 */
export interface SupplierAudit {
  cas: string;
  element: string;
  suppliers: string[];
  bestQuote?: PriceQuote;
  priceNote: string;
  /** "single-source" | "not-off-the-shelf" — CostAudit.cs:46,48 */
  risks: string[];
}

/** CostDoc — CostDoc.cs:18-25. Note the field is `substances`, not `molecules`. */
export interface CostDoc {
  id: string;
  projectId: string;
  type: string;
  substances: SupplierAudit[];
  generatedAt: string;
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
