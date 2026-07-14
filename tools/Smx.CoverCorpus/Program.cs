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

// (element symbol, group) — e.g. ("Y", "Rare earth")
var elementList = elements
    .Select(e => (Symbol: e!["element"]!.GetValue<string>(), Group: e["group"]?.GetValue<string>() ?? "marker"))
    .Distinct()
    .ToList();

// (element, molecular form) — e.g. ("Y", "2-ethylhexanoate"). Split the comma-separated `forms` cell.
var forms = elements
    .SelectMany(e => (e!["forms"]?.GetValue<string>() ?? "")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(f => f.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .Select(f => (Element: e["element"]!.GetValue<string>(), Form: f)))
    .Distinct()
    .ToList();

// (element, molecule) from the product listings — the most realistic decoys of all: real molecule names.
var molecules = products
    .Select(p => (Element: p!["element"]!.GetValue<string>(), Molecule: p["molecule"]!.GetValue<string>()))
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
