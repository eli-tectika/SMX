using System.Text.Json.Serialization;

namespace Smx.Domain.Records;

/// One cited source behind an analytical claim.
///
/// `Reference` is FREE TEXT the agent wrote — a label, not an identifier. `DocumentId` is the opposite:
/// the id `GET /documents/{id}` resolves, MINTED BY THE RETRIEVAL TOOL that returned the chunk (see
/// SearchTools / RegDocumentIdIndex), which is the only place the real id is known. It is never derived
/// from `Reference`: parsing that string would produce links that are usually right, and a chip that
/// opens the WRONG regulation is worse than one that opens nothing.
///
/// NULLABLE, and serialized EVEN WHEN NULL — Json.Options sets DefaultIgnoreCondition = WhenWritingNull,
/// so without [JsonIgnore(Never)] the key would simply vanish. The chip has to tell "this citation has no
/// document" from "this build/response never carried the field at all", and every citation written before
/// 2026-08-06 is the first kind — there are many, and they must keep rendering as deliberately inert
/// rather than as a link that might work. Absent id ⇒ inert chip ⇒ NO GUESS.
public sealed record Citation(
    string Source, string Reference, string RetrievedAt, string? Snippet = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DocumentId = null);

/// A component's production facts. BatchMassKg is MASS, deliberately — see OrderAmount. ppm is mg/kg, so a
/// batch VOLUME cannot yield an order amount without a density, and assuming water (1 L = 1 kg) mis-doses a
/// polymer by ~10% and gold by 19×. If the operator has a volume, they multiply by density and enter mass.
/// <param name="PhysicalState">The substrate's physical state — "liquid" | "solid" | "oil-soluble" |
/// "coating" (free text). It drives the pool pass's form-class choice (oil-soluble → organocomplex; solid
/// polymer → oxide/salt; coating → dispersible compound). Optional so existing records/eval fixtures keep
/// deserializing; the intake form now collects it.</param>
public sealed record ComponentSpec(
    string Id, string Material, string Application, IReadOnlyList<string> Markets, string Objective,
    double? BatchMassKg = null, string? PhysicalState = null);

public sealed record SubstanceSpec(string Element, string Form, string Cas);
public sealed record AppliedList(string ListId, string ComponentId, string Reason, Citation Citation);

public sealed record ElementPool(string Component, string Element, string Line, string Status, string? SignalNote = null); // Status: "V" | "L"

/// The physicist's MEASURED background for one element in one component. Together with the device LOD this
/// is what the ppm detection floor is computed from (DetectionFloor). It is measured data: like ElementPool,
/// it is not writable through chat (IntakeAnswers).
///
/// `Unit` is carried, not assumed — which is exactly why `Level` is NOT called `LevelPpm`. A field named for
/// one unit, sitting beside the field that says which unit it is in, is the very confusion this type exists
/// to prevent: DetectionFloor REFUSES to add a background to a LOD whose unit differs, because mixing counts
/// with ppm yields a number that looks perfectly reasonable and is simply wrong.
public sealed record MeasuredBackground(string Component, string Element, double Level, string Unit);

/// The XRF device the marker must be READ BY in deployment, and its per-element limit of detection (`Lod`,
/// unit-free for the same reason as `Level` above; `Unit` says what it is in).
/// The floor targets THIS device (UX spec §8: deployment-device-targeted floor), not an assumed lab unit.
public sealed record DeviceLod(string Element, double Lod, string Unit);
public sealed record XrfDevice(string Model, IReadOnlyList<DeviceLod> Lods);

public sealed record CandidateSubstance(
    string ComponentId, string Element, string Form, string Cas,
    string? ParticleSize, string? Solvent, bool Preferred, string Tier, string Rationale,
    IReadOnlyList<Citation> Citations); // Tier: "A" | "B" | "C"

public sealed class ConstraintsDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Constraints;
    public List<ComponentSpec> Components { get; set; } = [];
    public List<ElementPool> ElementPools { get; set; } = [];
    /// The two inputs of the ppm detection floor. Copied from the payload by code, never taken from the
    /// model's echo (IntakeAgent.RunAsync) — see MeasuredBackground above for what they are and why.
    public List<MeasuredBackground> MeasuredBackgrounds { get; set; } = [];
    public XrfDevice? Device { get; set; }
    /// Known-candidate mode (eval/integration): when non-empty, Discovery is bypassed and these
    /// become the candidates doc verbatim. Empty ⇒ the Discovery agent generates candidates.
    public List<CandidateSubstance> ProvidedCandidates { get; set; } = [];
    public List<string> ClientRestrictedList { get; set; } = [];
    /// Derived regulatory scope: which lists apply, per component (element gate entries use ComponentId="*").
    public List<AppliedList> DerivedScope { get; set; } = [];
}
