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
 * StageState.Status — src/Smx.Domain/Records/ProjectDoc.cs (StageStatus).
 *
 * FIVE VALUES, and there used to be ten. The five `awaiting-*` PARK states are deleted (execution-core
 * design §8, implemented 2026-08-06): the pipeline runs end to end on the best data it has, so a stage
 * status now answers exactly one question — DID THIS STAGE'S AGENT RUN — and nothing else.
 *
 * What a park used to carry is carried by better-placed facts:
 *   - "the operator has not authorised the analysis" → `analysisStartedAt` on the project
 *   - "a signature is outstanding"                   → the gate records, via the dashboard's
 *                                                      `outstandingSignatures`
 *   - "this rests on estimates or proposals"         → `DosingDoc.provisional` and `orderBlockers`
 *
 * THE TRAP THIS REPLACES ONE FAMILY OF BUGS WITH: `done` NO LONGER MEANS SIGNED. A project can have every
 * stage `done` with both gates unsigned and procurement refused. A screen that reads `done` as "finished"
 * tells the operator the opposite of the truth — which is the same shape as the four park-renders-as-
 * not-started bugs, pointing the other way.
 */
export type StageStatus =
  | 'pending'
  | 'running'
  | 'failed'
  | 'needs-review'
  | 'done';

export const BACKED_STAGES = ['intake', 'discovery', 'regulatory', 'matrix', 'dosing'] as const;
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
   * Drives the pool pass's form-class choice (oil-soluble → organocomplex; solid polymer →
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

/**
 * PoolSuggestion — src/Smx.Domain/Records/PoolDoc.cs.
 *
 * One proposed marker for one component: an ELEMENT and a FORM-CLASS, never a CAS — the exact form
 * and its check-digit-guarded CAS are Discovery's job. `formClass` is a closed enum the pool pass is
 * validated against (PoolAgent.FormClasses), which is why it is a union here and not a string.
 *
 * It is a HYPOTHESIS, corroborated or dropped downstream — the pool pass is allowed to draw on model
 * knowledge, unlike Discovery, so a suggestion may legitimately rest on no retrieved source at all.
 */
export interface PoolSuggestion {
  component: string;
  element: string;
  formClass: 'metal' | 'compound' | 'organocomplex';
  rationale: string;
  citations: Citation[];
  /** Set when the suggestion rests on model knowledge alone (execution-core-design §9). */
  uncited?: boolean;
}

/** PoolDoc — the proposed candidate pool, produced from the need alone before any XRF filter. */
export interface PoolDoc {
  projectId: string;
  suggestions: PoolSuggestion[];
}

/** Citation — src/Smx.Domain/Records/ConstraintsDoc.cs */
export interface Citation {
  source: string;
  reference: string;
  retrievedAt: string;
  snippet?: string;
  /**
   * The document this chunk was retrieved FROM, written by the retrieval tool — the only place the
   * real id is known (redesign spec §16.5).
   *
   * `string | null` and never optional-only: the backend serializes it even when null, so a chip can
   * tell "this citation predates the field" from "this build and that build have skewed". Both read
   * the same way — INERT — because the alternative is deriving an id by parsing `reference`, which is
   * free text the agent wrote, and a chip that opens the WRONG regulation is worse than one that
   * opens nothing. Most citations on the record carry no id and never will.
   */
  documentId?: string | null;
  /** The chunk anchor inside that document, where the retrieval tool knew one. */
  entryId?: string | null;
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

/**
 * CandidateSubstance — src/Smx.Domain/Records/ConstraintsDoc.cs.
 *
 * One proposed marker for ONE component. Candidates are per-component tracks: there is no
 * product-wide marker, and a UI that flattens these into one pool contradicts the architecture.
 *
 * `tier` is a severity ordering — A strong, B needs-validation, C excluded. `preferred` is the
 * agent's pick within a component. A web-only candidate is capped at tier B and can never be
 * `preferred` (DiscoveryAgent.Validate), so a preferred tier-A row is a claim about corpus
 * evidence, not a formatting flourish.
 */
export interface CandidateSubstance {
  componentId: string;
  element: string;
  form: string;
  cas: string;
  particleSize?: string;
  solvent?: string;
  preferred: boolean;
  tier: 'A' | 'B' | 'C';
  rationale: string;
  citations: Citation[];
}

/** CandidatesDoc — src/Smx.Domain/Records/CandidatesDoc.cs. 404s until Discovery has run. */
export interface CandidatesDoc {
  id: string;
  projectId: string;
  type: string;
  substances: CandidateSubstance[];
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
  /** Optional: a need-only project carries no operator element pool — the discovery agent proposes one instead. */
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
  /**
   * When the operator authorised the analysis; null until they press Start (ProjectDoc.AnalysisStartedAt).
   *
   * `null`, never `undefined`: the backend serializes it even when null precisely so the UI reads
   * "not started" off the wire instead of inferring it from an absent key. Typed non-optional here so a
   * screen cannot quietly treat a missing field as "started".
   */
  analysisStartedAt: string | null;
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
  /**
   * Null until the operator presses Start. The card needs it for the same reason the detail route does:
   * "created but never authorised" is a state, and it is not `pending` — nothing will dispatch those
   * pending stages until this is set.
   */
  analysisStartedAt: string | null;
  /** The run in flight, if any — the list endpoint projects the newest running run. */
  activeRun?: ActiveRun | null;
  /**
   * Both gate statuses, from GET /projects.
   *
   * The card CANNOT answer "is this project finished" without them any more. Every stage reaching `done`
   * used to imply the signatures were in, because the Decision stage only left `awaiting-VP` when the VP
   * signed. It does not imply that now — `done` means the agent ran — so a card reading stage statuses
   * alone would paint a fully-computed, entirely unsigned project as complete.
   */
  gates?: ProjectGates;
}

/**
 * GateDoc.Status per gate: "locked" until signed, "approved" after.
 *
 * ONE GATE, and it used to be two. The regulatory gate is not demoted but REMOVED (spec §16.4): the
 * matrix carries confidence and sources, and that is the review surface. The VP determination is now
 * the only human checkpoint before procurement — which is why the sign-off screen has to show the
 * evidence a judgement needs rather than a summary.
 */
export interface ProjectGates {
  vp: string;
}

/**
 * What is happening right now, in words. The stage pill says WHERE the project is; this says what
 * is being done there — the cheap answer to "where should I even look".
 *
 * `agent: null` means a deterministic stage, and the card must not imply a model was involved.
 */
export interface ActiveRun {
  stage: string;
  agent: string | null;
  lastStep: string | null;
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

/*
 * `RegulatoryGate` lived here — status, armable, blockers, approvedAt, approvedBy — over
 * `GET /projects/{id}/gate/regulatory` and `POST /projects/{id}/regulatory/approve`.
 *
 * BOTH ENDPOINTS ARE GONE (spec §16.4). The gate was removed rather than demoted, so there is no
 * record to project and nothing to sign. What survives is the pair of per-verdict acts the operator
 * still performs on that screen, and they became load-bearing rather than incidental:
 *
 *   POST …/regulatory/review          — a flagged finding was OPENED
 *   POST …/regulatory/determination   — the operator's ruling on one verdict, reason mandatory
 *
 * The arming check survives too, under a new name and a new owner: `EvidenceReview.Outstanding` now
 * refuses `POST /decision/determination` and `POST /orders/{cas}` while any live non-Pass verdict is
 * unopened. So the anti-rubber-stamping rule did not disappear with the gate — it moved to the two
 * irreversible acts, which is where §10 always said the preconditions belonged.
 */

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
/**
 * Who sells one substance, and what is risky about that. `risks` are "single-source" | "not-off-the-shelf".
 * Neither was ever derived from a price, which is why deleting the Cost stage cost this record nothing.
 */
export interface SupplierAudit {
  cas: string;
  element: string;
  suppliers: string[];
  risks: string[];
}

export interface DosingDoc {
  id: string;
  projectId: string;
  type: string;
  windows: PpmWindow[];
  codes: MarkerCode[];
  /** Availability per substance, from the reference catalog — formerly the payload of the Cost stage. */
  supply: SupplierAudit[];
  /**
   * TRUE when this dosing rests on something weaker than a signature or a measurement: a substance present
   * on the agent's PROPOSAL alone, or a window computed over a default detection floor rather than the
   * physicist's number.
   *
   * It is the ORDER-BLOCKING flag — procurement refuses over it. Serialized by the backend even when false
   * so the UI reads "not provisional" off the wire rather than inferring it from a missing key; a build
   * skew would otherwise turn an absent alarm into a clean bill of health.
   */
  provisional: boolean;
  /** One NAMED line per reason, never a count — "3 substances are provisional" is not actionable. */
  provisionalReasons: string[];
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

/* ---------------------------------------------------------------------------------------------------
   COST IS DELETED (redesign spec §6). PriceQuote / SupplierAudit / CostDoc lived here.

   The customer confirmed there are no price details, so the stage that existed to attach one is gone. What
   procurement actually acts on — who sells the substance and what is risky about that — survives as
   `SupplierAudit` inside DosingDoc, reachable on the wire as `DosingCells.suppliers` / `.risks`, and the
   ORDER AMOUNT was always in Dosing (`CodeMarker.compoundMassMg`), never in Cost.
   --------------------------------------------------------------------------------------------------- */


/* ---------------------------------------------------------------------------
   DECISION — src/Smx.Domain/Records/DecisionDoc.cs. The last stage of the journey.
   --------------------------------------------------------------------------- */

/**
 * Which criteria a row has actually cleared — booleans DecisionAssembler computes FROM THE RECORD
 * (a recommended determination, a dosable window, a priced audit), never asserted by an agent.
 *
 * There are three, not the four the old fixture drew. `xrf` and `compatibility` were never criteria
 * the record evaluates at this stage.
 */
export interface ClearedCriteria {
  regulatory: boolean;
  dosing: boolean;
  /**
   * Was `cost`. With no price data to be had, what this criterion can honestly assert is that somebody
   * SELLS the substance. Renamed rather than removed: silently dropping a criterion would shrink what the
   * VP signs over without anyone deciding to.
   */
  availability: boolean;
}

/**
 * Where each claim in a row came from — RECORD IDS, so every figure is traceable end-to-end (§3.5).
 *
 * TWO refs, not three. `audit` pointed at the cost document; with the supply audit folded into the dosing
 * doc it would be the same id as `window` on every row — a trace that traces to itself.
 */
export interface TraceRefs {
  verdict: string;
  window: string;
}

/** One substance's line in a component's decision. `determination` is the R.E.'s word, not the agent's. */
export interface DecisionRow {
  cas: string;
  element: string;
  determination: string;
  recommendedPpm: number;
  cleared: ClearedCriteria;
  traceability: TraceRefs;
}

/** The agent's RECOMMENDED code for a component. A PROPOSAL — never render it as a signature. */
export interface ProposedCode {
  ratioSignature: string;
  markerCas: string[];
  rationale: string;
}

/**
 * A component's decision.
 *
 * `proposedCode` is the AGENT's; `confirmedCode`/`confirmedBy`/`confirmedReason` are the VP's, written
 * only by POST …/decision/determination. `confirmedCode` serializes as an EXPLICIT `null` while
 * unconfirmed (a `JsonIgnore(Never)` attribute on the record exists precisely so the UI reads
 * "not signed yet" off the wire rather than inferring it from a missing key). Law 9 in a type: a UI
 * that gives these two fields one visual treatment is the agent signing the gate.
 */
export interface ComponentDecision {
  componentId: string;
  rows: DecisionRow[];
  proposedCode?: ProposedCode;
  /** Explicitly nullable — see the JsonIgnore note above. The ONLY `| null` in this group. */
  confirmedCode: string | null;
  confirmedBy?: string;
  confirmedReason?: string;
}

/**
 * Procurement is a STATE FLAG plus the substances actually ordered.
 *
 * `status` flips to `released` NOT by the signing call but by the ORCHESTRATOR reacting to the
 * approved gate (StageDispatcher). So a UI that signs must re-poll: release is eventually consistent,
 * and rendering the order controls straight off a 200 would offer an action the API still refuses.
 */
export interface ProcurementState {
  status: 'unreleased' | 'released';
  orderedCas: string[];
}

/** DecisionDoc — 404s until the Decision stage has assembled one. */
export interface DecisionDoc {
  id: string;
  projectId: string;
  type: string;
  components: ComponentDecision[];
  procurement: ProcurementState;
  generatedAt: string;
}

/**
 * GET /projects/{id}/gate/vp — DecisionEndpoints.cs.
 *
 * Same envelope as RegulatoryGate but DIFFERENT blocker semantics: these are plain-English sentences
 * meant to be displayed verbatim, not the parseable "unreviewed: {cas}|{comp}" strings the regulatory
 * gate emits. `armable` is computed server-side against the same rules the POST enforces, so the UI
 * must read it rather than tally anything browser-side — a gate that advertises a pen the POST refuses
 * is how a gate gets rubber-stamped.
 */
export interface VpGate {
  status: 'locked' | 'approved';
  armable: boolean;
  blockers: string[];
  /** Explicit JSON null until signed, not omitted — same wire contract as RegulatoryGate. */
  approvedAt?: string | null;
  /**
   * WHAT signed the last hard gate. `'operator'` is the human recording the VP's determination —
   * the only writer this endpoint has. `null` on an APPROVED gate means the record does not say,
   * which is a gate written before the field existed; it must render as unknown provenance and
   * never as a person. There is no auto-approve path for the VP gate today, and if one is ever
   * added it belongs in this union rather than in a second field.
   */
  approvedBy?: 'operator' | null;
}

/** One component's confirmed code. The VP may confirm the proposal or override with any REAL code. */
export interface VpConfirmation {
  componentId: string;
  code: string;
}

/**
 * POST /projects/{id}/decision/determination.
 *
 * `reason` is required for BOTH rulings — the backend 422s a blank one, and an override of a Fail
 * most of all. `confirmations` is required to approve and must name every component with a code that
 * exists in the DosingDoc for it; a signature over a nonexistent code is the false pass.
 */
export interface VpDeterminationRequest {
  determination: 'approved' | 'rejected';
  reason: string;
  confirmations?: VpConfirmation[];
}

/* ---------------------------------------------------------------------------
   The cross-project knowledge layer.

   These three surfaces rendered fixture data behind a MockBadge, on the stated grounds
   that the backend had no endpoint for them. That has not been true for some time:
   `KnowledgeEndpoints.cs` serves GET /marker-library, /learned-conclusions and
   /msds-registry — each taking a `?search=` parameter — plus POST /msds/{cas}/fetch and
   POST /msds/{cas}/upload, which acquire a safety sheet rather than sign one off.

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
 *
 * `reviewStatus` / `reviewedAt` are gone (design 2026-07-29, D8). MSDS-before-order survives — an
 * order still cannot proceed without a safety sheet — but its predicate is now *a validated,
 * indexed sheet exists*, which is a fact of the corpus. Reading it off a signature the operator had
 * to remember to record made the gate depend on the clerical act rather than on the document, and
 * left 40 substances stuck behind a checkbox nobody could tick.
 *
 * Which means the one thing this row now says about procurement is whether it has a `documentId`.
 */
export interface MsdsEntry {
  id: string;
  cas: string;
  supplier: string;
  version: string;
  date: string;
  linkedProjects: string[];
  /**
   * The `sds` document id of the corpus sheet this row was composed from — served, never derived
   * here. Absent for a governance-only row (manual/legacy), which has no sheet behind it and so
   * nothing to open — and which is exactly the row the order gate now refuses.
   */
  documentId?: string | null;
}

/* ---------------------------------------------------------------------------
   SDS acquisition — `POST /msds/{cas}/fetch` and `POST /msds/{cas}/upload`.
   Mirrors src/Smx.Domain/Tools/ISdsAcquisition.cs.
   --------------------------------------------------------------------------- */

/** One candidate the fetch tried, and what became of it. */
export interface SdsAttempt {
  url: string;
  supplier: string;
  outcome: string;
}

/**
 * What a fetch answers with.
 *
 * `unavailable` is an ANSWER, not an error — the request succeeded and the truthful result is that
 * no sheet could be obtained right now. `attempted` is the point of the whole contract: the
 * operator is owed what was tried and why each candidate failed, not merely that it did not work.
 */
export interface SdsEnsureResult {
  status: 'already-had' | 'fetched' | 'unavailable';
  registryId?: string | null;
  supplier?: string | null;
  revisionDate?: string | null;
  reason?: string | null;
  attempted?: SdsAttempt[] | null;
}

/**
 * What an upload answers with. `ok: false` is the validator's verdict on the file — an upload is a
 * fallback, never an override, so a document that is not a safety sheet for this substance does not
 * become one by arriving through a file picker.
 */
export interface SdsUploadResult {
  ok: boolean;
  reason?: string | null;
  registryId?: string | null;
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
  /**
   * The substance an `sds` row is about — served by the backend, never scraped out of `subtitle`.
   *
   * A gap row now carries an action ("fetch this sheet"), and the action needs a CAS. Parsing one
   * out of the subtitle is the mistake the citation chips deliberately refuse to make: it is right
   * until the wording changes, and then it fetches a sheet for the wrong substance.
   */
  cas?: string | null;
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

/* ---------------------------------------------------------------------------------------------------
   The unified project table — GET /projects/{id}/table (src/Smx.Domain/ProjectTable.cs).

   Every record from Discovery onward is keyed on (component, CAS), so the whole project record IS one
   wide table. Each phase contributes a column group; a phase screen renders its own group and the full
   matrix renders all of them, from ONE projection — the screen and the XLSX export cannot disagree.
   --------------------------------------------------------------------------------------------------- */

export interface DiscoveryCells {
  tier: string;
  preferred: boolean;
  rationale: string;
  sources: number;
}

/**
 * `proposedDetermination` and `determination` are SEPARATE fields, exactly as they are on the record.
 * Rendering them in one column would be the agent signing the regulatory gate at the presentation layer —
 * the thing the two-field split exists to prevent. Never collapse them.
 */
export interface RegulatoryCells {
  overall: VerdictStatus;
  dimensions: DimensionVerdict[];
  proposedDetermination: string | null;
  determination: string | null;
  evidenceReviewed: boolean;
}

/** Both bounds travel WHOLE: a ppm without its `kind` is a number whose provenance was thrown away. */
export interface DosingCells {
  floor: Bound;
  upper: Bound;
  recommendedPpm: number;
  compoundMassMg: number;
  suppliers: string[];
  risks: string[];
}

export interface OutcomeCells {
  inCode: string | null;
  ordered: boolean;
}

/**
 * One substance in one component, with each phase's contribution as a separate group.
 *
 * EVERY GROUP IS `| null`, NEVER OPTIONAL (`?`). The backend serializes these even when null so that
 * absence is explicit on the wire; typing them optional would let `undefined` (key absent — an older
 * bundle, a deploy skew) and `null` (the phase produced nothing) both arrive here meaning different
 * things, which is precisely the ambiguity the backend went out of its way to remove.
 *
 * `stoppedAt` is what distinguishes the two readings of an empty group: a phase that RAN and dropped this
 * row, versus a phase that has not run yet. Rendering those alike is the bug family this codebase has
 * shipped four times.
 */
export interface TableRow {
  componentId: string;
  cas: string;
  element: string;
  form: string;
  discovery: DiscoveryCells | null;
  regulatory: RegulatoryCells | null;
  dosing: DosingCells | null;
  outcome: OutcomeCells | null;
  stoppedAt: string | null;
  stoppedReason: string | null;
}

export interface ProjectTable {
  projectId: string;
  rows: TableRow[];
}

/* ---------------------------------------------------------------------------------------------------
   Amendments — GET/POST /projects/{id}/amendments (src/Smx.Backend/Api/AmendmentEndpoints.cs).
   --------------------------------------------------------------------------------------------------- */

/**
 * One requirement the operator changed after intake.
 *
 * `rerun` and `voidedSignatures` are what the amendment ACTUALLY cost at the time, recorded rather than
 * re-derived: the rerun map may legitimately change as the pipeline does, and the log has to say what
 * happened that day.
 */
export interface Amendment {
  at: string;
  componentId: string;
  field: string;
  from: string;
  to: string;
  reason: string;
  rerun: string[];
  voidedSignatures: string[];
}

/** The 409 body when an amendment would void a signature already on file. */
export interface AmendmentConflict {
  error: string;
  voids: string[];
  rerun: string[];
}
