namespace Smx.Domain.Intake;

/// <param name="Id">Stable, id-safe, and written into every dossier entry. Never renamed — a rename
/// orphans the entries of every project created before it.</param>
/// <param name="Prompt">What the agent asks, in the operator's language, not the system's.</param>
/// <param name="Why">Which downstream stage consumes the answer. This is in the AGENT's context: it is
/// how it judges whether an answer is sufficient rather than merely present.</param>
public sealed record IntakeQuestion(string Id, string Prompt, string Why);

/// The versioned catalogue of what a project must be asked before it can be created (design §4.1).
///
/// This list is the SINGLE source of the record_finding tool's description (see Description below) —
/// derived, never hand-listed beside it. The reason is a bug this codebase has already shipped: a
/// field added to an allowlist but missed in the prose description is a field the model never offers
/// to record, because it reads the description's list as exhaustive. The operator's answer is then
/// silently lost. It cost a dosing multiplier once.
public static class IntakeQuestions
{
    public static readonly IReadOnlyList<IntakeQuestion> All =
    [
        // ---- the process and the product -------------------------------------------------------
        new("raw-materials",
            "What raw materials go into the process?",
            "Discovery screens candidate chemistry against what is already in the material."),
        new("product-objectives",
            "What is the product, and what is the client actually trying to achieve by marking it?",
            "Sets each component's objective — brand go/no-go versus quantification."),
        new("process-steps",
            "What are the process steps that turn those materials into the finished product?",
            "Determines where in the line a marker can physically be introduced."),
        new("chemical-reactions",
            "What chemical reactions take place during the process?",
            "A marker that participates in a reaction is not a marker. Discovery needs this to exclude forms."),
        new("intermediates-byproducts",
            "What intermediates and by-products form along the way?",
            "By-product elements contaminate the XRF background and can invalidate a channel."),
        new("quality-parameters",
            "Which parameters govern the quality and consistency of the end product?",
            "Dosing must stay inside limits that do not disturb these."),
        new("qc-tests",
            "What analytical tests are run for quality control?",
            "Existing QC instrumentation may double as marker verification — and constrains what is detectable."),
        new("equipment",
            "What equipment and tooling does the process use, and could any of it introduce the marker?",
            "Decides whether marking needs new equipment, which drives cost and feasibility."),

        // ---- the marking problem ---------------------------------------------------------------
        new("marker-addition-point",
            "Given the client's objectives, where in the process would it be best to add the marker?",
            "The addition point determines the marker form and the achievable ppm."),
        new("durability-challenges",
            "What will challenge the marker's survival — heat, washing, UV, abrasion, shelf life?",
            "Discovery ranks forms by whether they survive the conditions named here."),
        new("detection-challenges",
            "What will make the marker hard to detect in the field?",
            "Feeds the ppm floor: a marker below the deployment device's LOD cannot be read."),

        // ---- structurally required by the pipeline ---------------------------------------------
        new("component-breakdown",
            "What are the separable parts of this product — bottle, lid, label, liquid?",
            "EVERYTHING downstream runs per component. There is no product-wide marker."),
        new("component-material",
            "What is each component made of?",
            "Material drives which marker forms are compatible."),
        new("component-application",
            "How is each component used — food contact, skin contact, non-contact, electronics?",
            "Application x markets selects the regulation lists the Regulatory gate screens against."),
        new("component-markets",
            "Which markets does each component ship to?",
            "A component with ZERO markets has an empty regulatory screen. That is a false-pass mechanism."),
        new("component-objective",
            "Per component: is this brand protection go/no-go, or does it need quantification?",
            "Flips the meaning of a conditional (L) XRF verdict at Background — an L fine for branding fails for quantification."),
        new("client-restrictions",
            "Does the client ban any elements of their own, beyond what regulation requires?",
            "Joins the product-wide element gate alongside REACH, RoHS, SVHC and Prop 65."),
        new("sample-status",
            "Are physical samples in hand, or are we working from literature for now?",
            "Sets background mode: measured versus provisional, and therefore how much weight a verdict carries."),
    ];

    public static IntakeQuestion? ById(string id) =>
        All.FirstOrDefault(q => string.Equals(q.Id, id, StringComparison.Ordinal));

    /// The question list as the MODEL is shown it. Derived, for the reason in the class comment above.
    public static string Description =>
        string.Join("; ", All.Select(q => $"{q.Id} ({q.Prompt})"));
}
