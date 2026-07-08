using System.Net.Http.Json;
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Eval;

var baseUrl = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("SMX_API_URL")
    ?? throw new InvalidOperationException("usage: Smx.Eval <api-base-url> [golden.json] — or set SMX_API_URL");
var goldenPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "golden", "starter.json");
var cases = JsonSerializer.Deserialize<List<GoldenCase>>(File.ReadAllText(goldenPath), Json.Options)!;
using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(120) };

var overall = new EvalReport();
foreach (var gc in cases)
{
    Console.WriteLine($"== case: {gc.Name}");
    var post = await http.PostAsJsonAsync("/projects", gc.ProjectPayload);
    post.EnsureSuccessStatusCode();
    var projectId = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetString()!;

    MatrixDoc? matrix = null;
    var deadline = DateTimeOffset.UtcNow.AddMinutes(20); // agent runs take minutes; poll patiently
    while (DateTimeOffset.UtcNow < deadline)
    {
        var resp = await http.GetAsync($"/projects/{projectId}/matrix");
        if (resp.IsSuccessStatusCode)
        {
            matrix = JsonSerializer.Deserialize<MatrixDoc>(await resp.Content.ReadAsStringAsync(), Json.Options);
            break;
        }
        var status = await http.GetFromJsonAsync<JsonElement>($"/projects/{projectId}");
        var stages = status.GetProperty("stages");
        Console.WriteLine($"   waiting... intake={S(stages, "intake")} screening={S(stages, "screening")} matrix={S(stages, "matrix")}");
        if (S(stages, "intake") == "failed" || S(stages, "screening") == "failed") break;
        await Task.Delay(TimeSpan.FromSeconds(15));
    }
    if (matrix is null) { Console.WriteLine($"   NO MATRIX for {gc.Name} — counting all cells as missing"); }

    var report = EvalMetrics.Score(gc.Expected, matrix?.Cells ?? []);
    Merge(overall, report);
    Print(gc.Name, report);
}
Print("TOTAL", overall);
File.WriteAllText("eval-report.json", JsonSerializer.Serialize(overall, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
Console.WriteLine("wrote eval-report.json");
return overall.FalsePassCount == 0 ? 0 : 2; // false-pass is the harm case: non-zero exit

static string S(JsonElement stages, string k) => stages.GetProperty(k).GetProperty("status").GetString()!;

static void Merge(EvalReport into, EvalReport from)
{
    foreach (var (k, v) in from.Tracks)
    {
        var t = into.Tracks.TryGetValue(k, out var e) ? e : into.Tracks[k] = new TrackScore();
        t.Total += v.Total; t.Agreed += v.Agreed;
    }
    into.FalsePassCount += from.FalsePassCount;
    into.UncitedCount += from.UncitedCount;
    into.MissingCount += from.MissingCount;
    into.Failures.AddRange(from.Failures);
}

static void Print(string name, EvalReport r)
{
    Console.WriteLine($"-- {name}");
    foreach (var (track, s) in r.Tracks)
        Console.WriteLine($"   {track}: {s.Agreed}/{s.Total} = {s.Agreement:P0}");
    Console.WriteLine($"   false-pass: {r.FalsePassCount}  uncited: {r.UncitedCount}  missing: {r.MissingCount}");
    foreach (var f in r.Failures) Console.WriteLine($"   ! {f}");
}
