using System.Globalization;
using Smx.Domain.Records;

namespace Smx.Domain;

/// WHAT A RERUN CHANGED — computed from the RECORD, by comparing the state before to the state after.
///
/// A rerun that silently mutates a record the operator has already read and reasoned about is worse than no
/// rerun at all (redesign spec §9.5). They signed off on a screen, went away for two days, amended one market,
/// and came back to a record that looks the same but isn't. So a rerun has to say what moved.
///
/// FOUR RULES, each of which is a bug this codebase would otherwise ship:
///
/// 1. THE DIFF IS COMPUTED, NOT NARRATED. Nothing here asks an agent what it changed. An agent's account of
///    its own edits is not evidence — it is the same class of claim as a fabricated citation, and it fails in
///    the same direction (it reports the change it INTENDED, not the one it made). Every line below is
///    derived from two snapshots of the persisted record.
///
/// 2. ABSENCE NEVER READS AS "NO CHANGE". <see cref="Diff.Comparable"/> is false when there was no prior
///    state on file, and <see cref="Diff.Summary"/> then says exactly that. An empty <see cref="Diff.Lines"/>
///    is therefore ambiguous ON ITS OWN and must never be rendered alone: "nothing changed" and "nothing can
///    be said about what changed" are opposite messages, and collapsing them into an empty list is how a
///    rerun that never happened comes to read as a rerun that changed nothing. `Summary` is never empty and
///    is the field that tells the three cases apart.
///
/// 3. SUBSTANCES ARE NAMED, NEVER COUNTED. "3 verdicts changed" is not something an operator can act on;
///    "Eu (14589-40-3) in 'bottle': Pass -> Fail" is. The count lives in the summary and the names live in
///    the lines, and the lines are not capped — hiding the fourteenth changed verdict behind an ellipsis
///    would hide exactly the one nobody expected.
///
/// 4. IT COMPARES SNAPSHOTS, NOT DOCUMENTS. The callers capture a <see cref="VerdictSnapshot"/> /
///    <see cref="DosingSnapshot"/> BEFORE the rerun. Holding the <see cref="VerdictDoc"/>s themselves would
///    make "before" depend on whether the store handed back a fresh graph or a live reference — and against a
///    store that hands back live references the rerun would mutate the past, producing a diff that reports no
///    change because both sides are the same object. The snapshots are immutable and carry only the fields
///    the comparison reads, so that failure is unrepresentable rather than store-dependent.
///
/// Pure and deterministic: no clock, no store, no I/O. <c>RerunDiffTests</c> is the whole test surface.
public static class RerunDiff
{
    /// What kind of movement a <see cref="Change"/> describes. Constants because they are compared across the
    /// runner and the tests, and a typo would be a change nobody filters for rather than a compile error.
    public static class Kinds
    {
        public const string Added = "added";
        public const string Removed = "removed";
        public const string StatusChanged = "status-changed";
        public const string DeterminationChanged = "determination-changed";
        public const string PpmChanged = "ppm-changed";
        public const string OrderAmountChanged = "order-amount-changed";
        public const string ProvisionalChanged = "provisional-changed";
    }

    /// One movement, structured. <paramref name="Subject"/> identifies the thing that moved well enough to
    /// find it in the record — "Eu (14589-40-3) in 'bottle'" — and <paramref name="Line"/> is the same fact
    /// rendered for a human, so a caller never has to reassemble a sentence from parts and get the wording
    /// wrong in a second place.
    public sealed record Change(string Kind, string Subject, string? Before, string? After, string Line);

    /// <param name="Comparable">false ⇒ there was no prior state on file. See rule 2: this is NOT the same
    /// as "nothing changed", and a renderer that treats an empty <paramref name="Lines"/> as the latter is
    /// wrong in the dangerous direction.</param>
    /// <param name="Summary">one sentence, ALWAYS non-empty, distinguishing the three outcomes:
    /// not comparable / comparable-and-unchanged / comparable-and-changed.</param>
    /// <param name="Lines">one named line per movement, in a stable order. Empty when nothing moved.</param>
    public sealed record Diff(
        bool Comparable, string Summary, IReadOnlyList<string> Lines, IReadOnlyList<Change> Changes)
    {
        /// Everything to show, headline first. Convenience for the callers that emit the diff as a sequence
        /// of trail steps; it exists so no caller invents its own way of putting `Summary` and `Lines`
        /// together and drops the summary on the unchanged path — which is precisely rule 2's failure.
        public IReadOnlyList<string> AllLines => [Summary, .. Lines];
    }

    // ---------------------------------------------------------------------------------------------
    // Verdicts.
    // ---------------------------------------------------------------------------------------------

    /// The fields of a <see cref="VerdictDoc"/> a rerun can move and an operator would need to be told about:
    /// the folded status the screen produced, and the ruling standing over it.
    ///
    /// <see cref="VerdictDoc.Overall"/> is a DERIVED fold over the dimensions, so it is captured as a value
    /// here rather than recomputed later against a document that has since been replaced.
    public sealed record VerdictSnapshot(
        string Cas, string ComponentId, string Element, VerdictStatus Overall, string? Determination)
    {
        public static VerdictSnapshot Of(VerdictDoc v) =>
            new(v.Cas, v.ComponentId, v.Element, v.Overall, v.Determination);

        public static IReadOnlyList<VerdictSnapshot> Of(IEnumerable<VerdictDoc> verdicts) =>
            [.. verdicts.Select(Of)];
    }

    /// Compare two verdict sets on (Cas, ComponentId).
    ///
    /// ORDINAL keys, matching <c>PipelineRunner.RunRegulatoryAsync</c>'s own `existing` set exactly. If the
    /// two disagreed on case-sensitivity the runner would consider a verdict present while this function
    /// called it added — a diff line for a screen that never ran.
    ///
    /// Reported: added, removed, and — for a substance present on both sides — a changed `Overall` or a
    /// changed `Determination`. A REMOVED verdict reads loudest of the three: a substance that was screened
    /// and now is not is a hole in the analysis, not a tidy-up.
    public static Diff OfVerdicts(
        IReadOnlyList<VerdictSnapshot>? before, IReadOnlyList<VerdictSnapshot> after)
    {
        // Null and EMPTY are treated identically, and deliberately: in both cases there is nothing on file to
        // compare against. The alternative — calling an empty `before` a real comparison — would render a
        // project's FIRST regulatory screen as "14 verdicts added", presenting an initial analysis as a
        // change to something the operator had previously read.
        if (before is null || before.Count == 0) return NoPriorState("verdict");

        var was = before.ToDictionary(Key, StringTupleComparer);
        var now = after.ToDictionary(Key, StringTupleComparer);
        var changes = new List<Change>();

        // Ordered by subject so two runs over the same movement produce byte-identical output — a diff that
        // reorders itself run to run is one nobody can eyeball for "did anything move since last time".
        foreach (var key in was.Keys.Union(now.Keys).OrderBy(k => $"{k.Item2}|{k.Item1}", StringComparer.Ordinal))
        {
            var had = was.GetValueOrDefault(key);
            var has = now.GetValueOrDefault(key);

            if (had is null && has is not null)
            {
                changes.Add(new Change(Kinds.Added, Subject(has), null, Status(has.Overall),
                    $"{Subject(has)}: newly screened — {Status(has.Overall)}."));
                continue;
            }
            if (had is not null && has is null)
            {
                changes.Add(new Change(Kinds.Removed, Subject(had), Status(had.Overall), null,
                    $"{Subject(had)}: NO LONGER SCREENED — it was {Status(had.Overall)}. " +
                    "There is now no verdict on file for this substance."));
                continue;
            }
            if (had is null || has is null) continue; // unreachable; the compiler cannot see it

            if (had.Overall != has.Overall)
                changes.Add(new Change(Kinds.StatusChanged, Subject(has),
                    Status(had.Overall), Status(has.Overall),
                    $"{Subject(has)}: {Status(had.Overall)} -> {Status(has.Overall)}."));

            // A determination is the OPERATOR'S ruling. A rerun that clears it (the fresh VerdictDoc an agent
            // writes carries `Determination = null`) has un-signed a human's decision, and that must be said
            // out loud rather than inferred from the gate going un-armed three screens away.
            if (!string.Equals(had.Determination, has.Determination, StringComparison.Ordinal))
                changes.Add(new Change(Kinds.DeterminationChanged, Subject(has),
                    Ruling(had.Determination), Ruling(has.Determination),
                    $"{Subject(has)}: determination {Ruling(had.Determination)} -> {Ruling(has.Determination)}."));
        }

        return Assemble(changes, "verdict", "verdicts");
    }

    // ---------------------------------------------------------------------------------------------
    // Dosing.
    // ---------------------------------------------------------------------------------------------

    /// One dosed substance, as far as a diff is concerned: the ppm the agent recommended, and the amount
    /// procurement would order for it.
    ///
    /// BOTH, and this is the reason the dosing diff exists at all. `batchMassKg` is the one amendment whose
    /// blast radius is Dosing ALONE (<see cref="RerunScope"/>), and it is a pure multiplier on the ORDER
    /// AMOUNT — it moves no ppm. A diff that compared only ppm windows would answer "nothing changed" to the
    /// one amendment guaranteed to reach this stage, which is the worst possible report: a confident
    /// all-clear over a procurement quantity that just doubled.
    public sealed record DosedSnapshot(
        string ComponentId, string Cas, string Element, double? RecommendedPpm, double? CompoundMassMg);

    public sealed record DosingSnapshot(IReadOnlyList<DosedSnapshot> Dosed, bool Provisional)
    {
        /// Folds the two lists a DosingDoc keeps separately — `Windows` (the ppm answer) and the markers
        /// inside `Codes` (the order amount) — onto one (ComponentId, Cas) key, because that pair is what an
        /// operator thinks of as "the dose for this substance here". A substance may appear in one and not
        /// the other (a window with no finalized code, a code marker with no window), so both sides are
        /// nullable rather than defaulted: a MISSING order amount is not an order amount of zero, and
        /// rendering it as 0 mg would report a substance nobody will order as one nobody needs to.
        public static DosingSnapshot Of(DosingDoc doc)
        {
            var ppm = doc.Windows.ToDictionary(w => (w.ComponentId, w.Cas), w => (double?)w.RecommendedPpm,
                StringTupleComparer);
            var mass = new Dictionary<(string, string), double?>(StringTupleComparer);
            var element = new Dictionary<(string, string), string>(StringTupleComparer);
            foreach (var w in doc.Windows) element[(w.ComponentId, w.Cas)] = w.Element;
            foreach (var code in doc.Codes)
                foreach (var m in code.Markers)
                {
                    var key = (code.ComponentId, m.Cas);
                    // SUMMED, not last-wins. One substance can be a marker in two codes for the same
                    // component, and procurement orders the total; keeping only the last would under-report
                    // the amount, which is the direction that leaves a batch short.
                    mass[key] = (mass.GetValueOrDefault(key) ?? 0) + m.CompoundMassMg;
                    element.TryAdd(key, m.Element);
                }

            return new DosingSnapshot(
                [.. ppm.Keys.Union(mass.Keys)
                    .OrderBy(k => $"{k.Item1}|{k.Item2}", StringComparer.Ordinal)
                    .Select(k => new DosedSnapshot(
                        k.Item1, k.Item2, element.GetValueOrDefault(k, ""),
                        ppm.GetValueOrDefault(k), mass.GetValueOrDefault(k)))],
                doc.Provisional);
        }
    }

    /// Compare two dosings on (ComponentId, Cas): substances added, substances dropped, a recommended ppm
    /// that moved, an order amount that moved, and the provisional flag flipping.
    ///
    /// The provisional flip is reported FIRST when it turns ON, because it is the only line here that changes
    /// what the record is ALLOWED to do — a provisional dosing cannot release an order — rather than what it
    /// says.
    public static Diff OfDosing(DosingSnapshot? before, DosingSnapshot after)
    {
        // Same rule as verdicts: no prior document ⇒ no comparison. The first dosing a project ever produces
        // is not "every substance added"; it is the baseline everything after it is measured against.
        if (before is null) return NoPriorState("dosing");

        var was = before.Dosed.ToDictionary(k => (k.ComponentId, k.Cas), StringTupleComparer);
        var now = after.Dosed.ToDictionary(k => (k.ComponentId, k.Cas), StringTupleComparer);
        var changes = new List<Change>();

        if (before.Provisional != after.Provisional)
            changes.Add(after.Provisional
                ? new Change(Kinds.ProvisionalChanged, "this dosing", "final", "provisional",
                    "This dosing is now PROVISIONAL — it rests on something weaker than a signature or a " +
                    "measurement, and it can no longer release an order. Read its reasons.")
                : new Change(Kinds.ProvisionalChanged, "this dosing", "provisional", "final",
                    "This dosing is no longer provisional — every input it rests on is now on file."));

        foreach (var key in was.Keys.Union(now.Keys).OrderBy(k => $"{k.Item1}|{k.Item2}", StringComparer.Ordinal))
        {
            var had = was.GetValueOrDefault(key);
            var has = now.GetValueOrDefault(key);

            if (had is null && has is not null)
            {
                changes.Add(new Change(Kinds.Added, Subject(has), null, Ppm(has.RecommendedPpm),
                    $"{Subject(has)}: newly dosed at {Ppm(has.RecommendedPpm)}."));
                continue;
            }
            if (had is not null && has is null)
            {
                changes.Add(new Change(Kinds.Removed, Subject(had), Ppm(had.RecommendedPpm), null,
                    $"{Subject(had)}: NO LONGER DOSED — it was {Ppm(had.RecommendedPpm)}."));
                continue;
            }
            if (had is null || has is null) continue; // unreachable; the compiler cannot see it

            // Compared on the RENDERED value, not the raw double. Two things follow, both wanted: a
            // difference invisible at display precision produces no line (a row reading "4.2 -> 4.2" is worse
            // than silence, because the operator hunts for a change that is not there), and a change that IS
            // reported is always one they can see in the record. Raw `!=` on a double round-tripped through
            // JSON and recomputed by an agent would fire on noise.
            if (Ppm(had.RecommendedPpm) != Ppm(has.RecommendedPpm))
                changes.Add(new Change(Kinds.PpmChanged, Subject(has),
                    Ppm(had.RecommendedPpm), Ppm(has.RecommendedPpm),
                    $"{Subject(has)}: {Ppm(had.RecommendedPpm)} -> {Ppm(has.RecommendedPpm)}."));

            if (Mass(had.CompoundMassMg) != Mass(has.CompoundMassMg))
                changes.Add(new Change(Kinds.OrderAmountChanged, Subject(has),
                    Mass(had.CompoundMassMg), Mass(has.CompoundMassMg),
                    $"{Subject(has)}: order amount {Mass(had.CompoundMassMg)} -> {Mass(has.CompoundMassMg)}."));
        }

        return Assemble(changes, "dosing entry", "dosing entries");
    }

    // ---------------------------------------------------------------------------------------------
    // Rendering. Every string an operator reads is built here, once.
    // ---------------------------------------------------------------------------------------------

    /// The "no prior state" answer, and the whole of rule 2. It is a SENTENCE, not an empty result: a caller
    /// that renders this gets a claim it can neither mistake for "nothing changed" nor quietly drop.
    private static Diff NoPriorState(string noun) => new(
        Comparable: false,
        Summary: $"No prior state to compare — there was no {noun} on file before this run, so nothing can " +
                 "be said about what changed.",
        Lines: [],
        Changes: []);

    /// Both forms of the noun are passed in rather than an "s" being appended: "dosing entrys" is the kind of
    /// detail that makes an operator trust a machine-written line less than they should.
    private static Diff Assemble(List<Change> changes, string singular, string plural) => new(
        Comparable: true,
        // The unchanged case gets its OWN sentence rather than an empty list. "Nothing changed" is a real
        // finding — it is what tells the operator their sign-off still describes the record — and it is a
        // different claim from "nothing could be compared", which is what an empty result would blur it into.
        Summary: changes.Count == 0
            ? $"Reran; no {singular} changed."
            : $"Reran; {changes.Count} {(changes.Count == 1 ? singular : plural)} changed.",
        Lines: [.. changes.Select(c => c.Line)],
        Changes: changes);

    private static string Subject(VerdictSnapshot v) => $"{v.Element} ({v.Cas}) in '{v.ComponentId}'";

    private static string Subject(DosedSnapshot d) =>
        d.Element.Length > 0 ? $"{d.Element} ({d.Cas}) in '{d.ComponentId}'" : $"{d.Cas} in '{d.ComponentId}'";

    /// The fallback is the enum's own name rather than a guess. A status this method does not know is a
    /// status nobody should be shown a friendly euphemism for — printing `Conditional` verbatim is honest,
    /// and mapping an unrecognised value onto "Pass" is the quiet reading of an alarm.
    private static string Status(VerdictStatus s) => s switch
    {
        VerdictStatus.Pass => "Pass",
        VerdictStatus.Conditional => "Conditional",
        VerdictStatus.NeedsReview => "Needs review",
        VerdictStatus.Fail => "Fail",
        _ => s.ToString(),
    };

    /// A null determination is "not ruled", spelled out. Rendering it as an empty string would produce
    /// "determination  -> rejected", which reads as a formatting bug rather than as the fact that nobody had
    /// ruled — and the absence of a ruling is the thing the regulatory gate turns on.
    private static string Ruling(string? determination) => determination switch
    {
        null => "not ruled",
        Determinations.Recommended => Determinations.Recommended,
        Determinations.Rejected => Determinations.Rejected,
        // A non-canonical string is a hand-edited document. Quoted so it is visibly not one of the two legal
        // rulings, rather than silently reading like one.
        var other => $"'{other}'",
    };

    private static string Ppm(double? ppm) =>
        ppm is null ? "no ppm window" : $"{ppm.Value.ToString("0.###", CultureInfo.InvariantCulture)} ppm";

    private static string Mass(double? mg) =>
        mg is null ? "not ordered" : $"{mg.Value.ToString("0.###", CultureInfo.InvariantCulture)} mg";

    private static (string, string) Key(VerdictSnapshot v) => (v.Cas, v.ComponentId);

    /// Ordinal on both members. The default tuple comparer is already ordinal for strings; naming it here is
    /// so a later "let's be lenient about casing" edit has to argue with a comment. Leniency would MERGE two
    /// distinct component ids, and a diff that merges rows reports a change to the wrong substance.
    private static readonly IEqualityComparer<(string, string)> StringTupleComparer =
        EqualityComparer<(string, string)>.Default;
}
