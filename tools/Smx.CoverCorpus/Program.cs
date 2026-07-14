using System.Text.Json;
using System.Text.Json.Nodes;

// Generates src/Smx.SearchProxy/Config/cover-corpus.json from the seeded reference catalog.
//
// The decoys have to look like the real thing. A real Discovery query is a question about an element's
// molecular forms, their properties, or where to buy them — so the corpus is exactly that question, asked
// about every element and form in the catalog. The result is a chemically plausible haystack: Brave sees a
// stream of taggant-chemistry questions spanning the whole catalog and cannot tell which one a live project
// actually asked.
//
// Usage:
//   dotnet run --project tools/Smx.CoverCorpus -- \
//     src/Smx.Functions/Reference/Seed src/Smx.SearchProxy/Config/cover-corpus.json

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Smx.CoverCorpus <seed-dir> <output-json>");
    return 1;
}
var (seedDir, outPath) = (args[0], args[1]);

var elements = JsonNode.Parse(File.ReadAllText(Path.Combine(seedDir, "catalog-elements.json")))!.AsArray();
var products = JsonNode.Parse(File.ReadAllText(Path.Combine(seedDir, "catalog-products.json")))!.AsArray();

// Some catalog rows model an element GROUP rather than a single element — "Tb/Dy/Ho", "Sn/Sb", "Ti/Zr". That
// is how the reference data legitimately expresses a family for the compatibility matrix, and the seed files
// are right to do it. But a decoy is not reference data. A real Discovery query always names exactly ONE
// element, because it is drawn from a component's XRF-derived element pool — so a decoy reading "Tb/Dy/Ho
// marker molecular forms" is a query no real project could ever have asked. It is not cover, it is a tell: an
// observer who spots the grouped shape discards every such decoy as machine-generated-and-never-real, and the
// anonymity set shrinks by exactly that much. Split the group; emit one decoy per symbol.
//
// This applies only to the element-SYMBOL position. The `group` label ("Rare earth", "Refractory/TM") names a
// chemical family and reads naturally in a query, slash and all — it is left intact.
static string[] Symbols(string element) =>
    element.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

// A grouped row also ANNOTATES a form with the element it actually belongs to: the Cr/Mn row's
// "octoate (Mn)" is Mn's octoate, not Cr's. Cross-producting that form across the whole group would invent
// "Cr octoate (Mn)" — a form bound to the wrong element, self-contradicting on its face, and a query no
// chemist would ever type. That is the same tell in miniature. So honour the qualifier: bind the form to its
// owner alone, and drop the now-redundant annotation from the text ("Mn octoate", not "Mn octoate (Mn)").
//
// A parenthetical that is NOT one of the row's own symbols is a formula, not an owner — "oxide (V2O5)",
// "oxide (MoO3)" — and is left exactly as it is, because "V oxide (V2O5)" is a perfectly natural search.
static (string Form, string? Owner) BindOwner(string form, string[] symbols)
{
    var open = form.LastIndexOf('(');
    if (form.EndsWith(')') && open > 0)
    {
        var inner = form[(open + 1)..^1].Trim();
        if (symbols.Contains(inner, StringComparer.Ordinal))
            return (form[..open].Trim(), inner);
    }
    return (form, null);
}

// (element symbol, group) — e.g. ("Y", "Rare earth")
var elementList = elements
    .SelectMany(e => Symbols(e!["element"]!.GetValue<string>())
        .Select(symbol => (Symbol: symbol, Group: e["group"]?.GetValue<string>() ?? "marker")))
    .Distinct()
    .ToList();

// (element, molecular form) — e.g. ("Y", "2-ethylhexanoate"). Split the comma-separated `forms` cell, and
// cross every form with every element the row covers: an "Sn/Sb" row's forms belong to Sn and to Sb alike.
var forms = elements
    .SelectMany(e =>
    {
        var symbols = Symbols(e!["element"]!.GetValue<string>());
        return (e["forms"]?.GetValue<string>() ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(f => f.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .SelectMany(f =>
            {
                var (form, owner) = BindOwner(f, symbols);
                return owner is null
                    ? symbols.Select(symbol => (Element: symbol, Form: form))
                    : new[] { (Element: owner, Form: form) };
            });
    })
    .Distinct()
    .ToList();

// (element, molecule) from the product listings — the most realistic decoys of all: real molecule names. The
// product rows carry grouped elements too ("Sn/Sb", "Ti/Zr"), so they need the same split.
var molecules = products
    .SelectMany(p => Symbols(p!["element"]!.GetValue<string>())
        .Select(symbol => (Element: symbol, Molecule: p["molecule"]!.GetValue<string>())))
    .Distinct()
    .ToList();

string[] substrates = ["polyethylene", "PET", "HDPE", "polypropylene", "paper label", "solvent ink", "glass"];

var candidateForms = elementList
    .SelectMany(e => new[]
    {
        $"{e.Symbol} marker molecular forms and CAS numbers",
        $"{e.Symbol} organometallic forms for XRF tagging",
        $"{e.Group} taggant candidates {e.Symbol} available forms",
    })
    .ToList();

var formProperties = forms
    .SelectMany(f => substrates.Take(3).Select(s => $"{f.Element} {f.Form} solubility and dispersion in {s}"))
    .Concat(molecules.Select(m => $"{m.Molecule} XRF detection limit and thermal stability"))
    .ToList();

var supplierAvailability = molecules
    .SelectMany(m => new[]
    {
        $"{m.Molecule} suppliers and purity grades",
        $"{m.Element} taggant precursor availability research quantities",
    })
    .ToList();

var corpus = new Dictionary<string, string[]>
{
    ["discovery.candidate_forms"] = candidateForms.Distinct().Order().ToArray(),
    ["discovery.form_properties"] = formProperties.Distinct().Order().ToArray(),
    ["discovery.supplier_availability"] = supplierAvailability.Distinct().Order().ToArray(),
};

File.WriteAllText(outPath, JsonSerializer.Serialize(corpus, new JsonSerializerOptions { WriteIndented = true }));
foreach (var (intent, qs) in corpus) Console.WriteLine($"{intent}: {qs.Length} decoys");
return 0;
