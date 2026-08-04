using Azure.Identity;
using Azure.Storage.Files.DataLake;
using Microsoft.Azure.Cosmos;
using Smx.CustomerDataLoad;
using Smx.Domain;
using Smx.Infrastructure;

// Loads customer-supplied source data into Azure WITHOUT it ever entering the repo.
//
// The data this reads is client-confidential (named projects, concentrations, batch numbers), so it is
// passed by PATH and never committed. The code is the deliverable; the data is not. Everything it writes
// is derived from the real domain types, so a schema change breaks this build rather than quietly
// writing knowledge nothing can read back.
//
//   dotnet run --project tools/Smx.CustomerDataLoad -- \
//     --source "/mnt/c/Users/me/Downloads/customer-drop" \
//     --cosmos "https://cosmos-smx-dev-lmxnb.documents.azure.com:443/" \
//     --storage stsmxdevlmxnb \
//     [--database smx] [--container learned-conclusions] [--dry-run]
//
// --dry-run reads and maps everything, prints exactly what WOULD be written, and touches nothing.
// Run it first: it is also the report of what the workbooks do and do not contain.

var opts = Args.Parse(args);
if (opts is null) return 1;

var source = new DirectoryInfo(opts.Source);
if (!source.Exists)
{
    Console.Error.WriteLine($"--source '{opts.Source}' does not exist.");
    return 1;
}

var createdAt = DateTimeOffset.UtcNow.ToString("O");
var all = new List<Smx.Domain.Records.LearnedConclusionDoc>();
var held = new List<string>();

// --- workbooks -------------------------------------------------------------------------------
var bg = Find(source, "Markers Assessment", ".xlsx");
if (bg is not null)
{
    var rows = Sheets.ReadBackground(bg.FullName);
    var mapped = KnowledgeMapper.FromBackground(rows, createdAt);
    Console.WriteLine($"XRF background : {rows.Count} verdicts read → {mapped.Conclusions.Count} conclusions, {mapped.Held.Count} held");
    all.AddRange(mapped.Conclusions);
    held.AddRange(mapped.Held);
}
else Console.WriteLine("XRF background : no 'Markers Assessment*.xlsx' in --source, skipping");

var poly = Find(source, "Polymers DB", ".xlsx");
if (poly is not null)
{
    var rows = Sheets.ReadPolymers(poly.FullName);
    var mapped = KnowledgeMapper.FromPolymers(rows, createdAt);
    Console.WriteLine($"Polymers DB    : {rows.Count} projects read → {mapped.Conclusions.Count} conclusions, {mapped.Held.Count} held");
    all.AddRange(mapped.Conclusions);
    held.AddRange(mapped.Held);
}
else Console.WriteLine("Polymers DB    : no 'Polymers DB*.xlsx' in --source, skipping");

// --- documents -------------------------------------------------------------------------------
var pdfs = source.GetFiles("*.pdf", SearchOption.AllDirectories)
    .Select(f => (File: f, Kind: Documents.Classify(f.Name)))
    .OrderBy(x => x.Kind).ThenBy(x => x.File.Name).ToList();
Console.WriteLine($"Documents      : {pdfs.Count} PDF(s) — " +
    string.Join(", ", pdfs.GroupBy(p => p.Kind).Select(g => $"{g.Count()} {g.Key}")));

// A duplicate id inside one run means two source rows collapsed onto the same key: the LAST would
// silently win. Cosmos cannot detect it (an upsert is a legal overwrite), so it is caught here.
var collisions = all.GroupBy(d => d.Id).Where(g => g.Count() > 1).ToList();
foreach (var c in collisions)
    Console.Error.WriteLine($"  ! id collision ({c.Count()}x): {c.Key}");

Console.WriteLine();
Console.WriteLine($"TOTAL          : {all.Count} conclusions to upsert " +
                  $"({all.GroupBy(d => d.Kind).Select(g => $"{g.Count()} {g.Key}").Aggregate((a, b) => a + ", " + b)})");
if (held.Count > 0)
{
    Console.WriteLine($"HELD BACK      : {held.Count} — not loaded, listed below");
    foreach (var h in held.Take(opts.DryRun ? int.MaxValue : 10)) Console.WriteLine($"  - {h}");
}

if (opts.DryRun)
{
    Console.WriteLine();
    Console.WriteLine("--dry-run: nothing was written. Sample of what would be:");
    foreach (var d in all.Take(3)) Console.WriteLine($"  [{d.Kind}] {d.Id}\n      {d.Finding}");
    return collisions.Count > 0 ? 1 : 0;
}

if (collisions.Count > 0)
{
    Console.Error.WriteLine("Refusing to write: resolve the id collisions above first.");
    return 1;
}

// --- write -----------------------------------------------------------------------------------
var credential = new DefaultAzureCredential();

if (opts.Cosmos is not null && all.Count > 0)
{
    // The SAME serializer the backend uses. With the SDK default, every document would be written
    // PascalCase and no query in the system would ever find it — see SystemTextJsonCosmosSerializer.
    using var cosmos = new CosmosClient(opts.Cosmos, credential,
        new CosmosClientOptions { Serializer = new SystemTextJsonCosmosSerializer(Json.Options) });
    var container = cosmos.GetContainer(opts.Database, opts.Container);

    var written = 0;
    foreach (var doc in all)
    {
        await container.UpsertItemAsync(doc, new PartitionKey(doc.Kind));
        if (++written % 50 == 0) Console.WriteLine($"  … {written}/{all.Count}");
    }
    Console.WriteLine($"Cosmos         : upserted {written} conclusions into {opts.Database}/{opts.Container}");
}

if (opts.Storage is not null && pdfs.Count > 0)
{
    var lake = new DataLakeServiceClient(new Uri($"https://{opts.Storage}.dfs.core.windows.net"), credential);
    var fs = lake.GetFileSystemClient("bronze");
    foreach (var (file, kind) in pdfs)
    {
        var path = $"{Documents.PrefixFor(kind)}/{file.Name}";
        await using var stream = file.OpenRead();
        await fs.GetFileClient(path).UploadAsync(stream, overwrite: true);
        Console.WriteLine($"  bronze/{path}");
    }
    Console.WriteLine($"Bronze         : uploaded {pdfs.Count} document(s)");
}

return 0;

static FileInfo? Find(DirectoryInfo dir, string startsWith, string extension) =>
    dir.GetFiles($"*{extension}", SearchOption.AllDirectories)
       .FirstOrDefault(f => f.Name.StartsWith(startsWith, StringComparison.OrdinalIgnoreCase));

file sealed record Options(
    string Source, string? Cosmos, string? Storage, string Database, string Container, bool DryRun);

file static class Args
{
    public static Options? Parse(string[] args)
    {
        string? source = null, cosmos = null, storage = null;
        string database = "smx", container = "learned-conclusions";
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source": source = Next(args, ref i); break;
                case "--cosmos": cosmos = Next(args, ref i); break;
                case "--storage": storage = Next(args, ref i); break;
                case "--database": database = Next(args, ref i) ?? database; break;
                case "--container": container = Next(args, ref i) ?? container; break;
                case "--dry-run": dryRun = true; break;
                default:
                    Console.Error.WriteLine($"unknown argument '{args[i]}'");
                    return null;
            }
        }

        if (source is null)
        {
            Console.Error.WriteLine(
                "usage: --source <dir> [--cosmos <endpoint>] [--storage <account>] " +
                "[--database smx] [--container learned-conclusions] [--dry-run]");
            return null;
        }
        if (!dryRun && cosmos is null && storage is null)
        {
            Console.Error.WriteLine("nothing to do: pass --cosmos and/or --storage, or --dry-run.");
            return null;
        }
        return new Options(source, cosmos, storage, database, container, dryRun);
    }

    private static string? Next(string[] args, ref int i) => ++i < args.Length ? args[i] : null;
}

file static class Documents
{
    /// COA and SDS are told apart by filename because that is the only signal the drop carries — the
    /// documents themselves are supplier-formatted and share no reliable marker. An unrecognised file
    /// is 'unclassified' rather than being guessed into one of the two: a COA filed as a safety sheet
    /// would let a missing MSDS read as satisfied, and MSDS-before-order is a hard gate.
    public static string Classify(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        if (n.Contains("coa") || n.Contains("certificate")) return "coa";
        if (n.Contains("sds") || n.Contains("msds")) return "sds";
        return "unclassified";
    }

    /// Certificates go to the prefix CoaDocumentProvider lists, taken from the provider itself — a
    /// loader writing one path while the catalog reads another produces an empty library that looks
    /// exactly like "nothing was uploaded".
    public static string PrefixFor(string kind) => kind == "coa"
        ? Smx.Domain.Documents.CoaDocumentProvider.Prefix
        : $"customer-documents/{kind}";
}
