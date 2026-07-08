// tools/Smx.ReferenceData.Transform/Program.cs
using System.Text.Json;
using ClosedXML.Excel;
using Smx.Functions.Reference.Domain;
using Smx.ReferenceData.Transform;

// Usage: dotnet run --project tools/Smx.ReferenceData.Transform -- <dataDir> <outDir> [datasetVersion]
var dataDir = args.Length > 0 ? args[0] : "data";
var outDir = args.Length > 1 ? args[1] : "src/Smx.Functions/Reference/Seed";
var version = args.Length > 2 ? args[2] : "2026-07";
Directory.CreateDirectory(outDir);

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
};

void Write<T>(string file, IReadOnlyList<T> docs)
{
    var path = Path.Combine(outDir, file);
    File.WriteAllText(path, JsonSerializer.Serialize(docs, jsonOpts));
    Console.Error.WriteLine($"  wrote {docs.Count,4}  {file}");
}

var compatXlsx = Path.Combine(dataDir, "SMX Marker Compatibility Knowledge Base.xlsx");
var supXlsx = Path.Combine(dataDir, "SMX Marker Suppliers - Comprehensive.xlsx");

Console.Error.WriteLine($"Reading {compatXlsx}");
using (var wb = new XLWorkbook(compatXlsx))
{
    var rules = Mappers.CompatibilityRules(SheetReader.Read(wb.Worksheet("Compatibility Rules"), 1));
    var gold = Mappers.GoldSolubility(SheetReader.Read(wb.Worksheet("Gold Solubility Data"), 2));
    var icp = Mappers.IcpInterference(SheetReader.Read(wb.Worksheet("ICP Interference"), 2));
    var biblio = Mappers.Bibliography(SheetReader.Read(wb.Worksheet("Reference Library"), 2));

    Write("compatibility-rules.json", rules);
    Write("gold-solubility.json", gold);
    Write("icp-interference.json", icp);
    Write("bibliography.json", biblio);
    Write("xrf-lines.json", Array.Empty<CompatibilityDoc>());   // follow-on: XrfLines mapper deferred
    Console.Error.WriteLine("  NOTE: xrf-lines.json written EMPTY (mapper deferred).");

    var chunks = Mappers.SearchChunks(rules, biblio, $"compatibility-{version}");
    Write("search-chunks.json", chunks);
}

Console.Error.WriteLine($"Reading {supXlsx}");
using (var wb = new XLWorkbook(supXlsx))
{
    Write("suppliers.json", Mappers.Suppliers(SheetReader.Read(wb.Worksheet("Master Supplier DB"), 1)));
    Write("catalog-products.json", Mappers.CatalogProducts(SheetReader.Read(wb.Worksheet("Marker Products & Pricing"), 1)));
    Write("catalog-elements.json", Mappers.CatalogElements(SheetReader.Read(wb.Worksheet("Marker Elements Reference"), 1)));
    Console.Error.WriteLine("  NOTE: supplementary supplier lists + Element×Form matrix deferred.");
}

Console.Error.WriteLine("Done.");
