using System.Net.Http.Json;
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Eval;

var baseUrl = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("SMX_API_URL")
    ?? throw new InvalidOperationException("usage: Smx.Eval <api-base-url> [golden.json] — or set SMX_API_URL");
var goldenPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "golden", "starter.json");
var cases = JsonSerializer.Deserialize<List<GoldenCase>>(File.ReadAllText(goldenPath), Json.Options)!;
// Trailing slash is load-bearing: request paths are RELATIVE ("projects/…"), so a slash-less base
// ("…/api") would have its last segment replaced, and a rooted path ("/projects/…") would discard the
// gateway's /api prefix entirely. Relative-on-slashed-base is the one combination that survives both
// direct (":5169/") and gateway-fronted ("…/api/") bases.
using var http = new HttpClient { BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), Timeout = TimeSpan.FromSeconds(120) };

var overall = new EvalReport();
foreach (var gc in cases)
{
    Console.WriteLine($"== case: {gc.Name}");
    var post = await http.PostAsJsonAsync("projects", gc.ProjectPayload);
    post.EnsureSuccessStatusCode();
    var projectId = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetString()!;

    MatrixDoc? matrix = null;
    var deadline = DateTimeOffset.UtcNow.AddMinutes(20); // agent runs take minutes; poll patiently
    while (DateTimeOffset.UtcNow < deadline)
    {
        var resp = await http.GetAsync($"projects/{projectId}/matrix");
        if (resp.IsSuccessStatusCode)
        {
            matrix = JsonSerializer.Deserialize<MatrixDoc>(await resp.Content.ReadAsStringAsync(), Json.Options);
            break;
        }
        var status = await http.GetFromJsonAsync<JsonElement>($"projects/{projectId}");
        var stages = status.GetProperty("stages");
        Console.WriteLine($"   waiting... intake={S(stages, "intake")} discovery={S(stages, "discovery")} regulatory={S(stages, "regulatory")} matrix={S(stages, "matrix")}");
        if (S(stages, "intake") == "failed" || S(stages, "discovery") == "failed" || S(stages, "regulatory") == "failed") break;
        await Task.Delay(TimeSpan.FromSeconds(15));
    }
    if (matrix is null) { Console.WriteLine($"   NO MATRIX for {gc.Name} — counting all cells as missing"); }

    var report = EvalMetrics.Score(gc.Expected, matrix?.Cells ?? []);

    // Design-§9 Dosing invariants, when the case got that far. The harness does not enter determinations,
    // sign the gate, or record loadings, so most cases never reach Dosing — a 404 here is expected and
    // scores nothing. When a DosingDoc DOES exist, an invariant breach in it is a harm case: it counts as a
    // FALSE PASS and trips the non-zero exit, exactly like a matrix false-pass.
    // A transport failure here (timeout, reset — seen live during an ACA revision swap) must not abort
    // the harness: the matrix half of this case is already scored, and losing the whole report to an
    // optional read would hide it. Not silent, though — the skip is printed, because "dosing unchecked"
    // and "dosing checked clean" must never look the same.
    DosingDoc? dosing = null;   // hoisted: the decision invariants below validate signed codes against it
    try
    {
        var dosingResp = await http.GetAsync($"projects/{projectId}/dosing");
        if (dosingResp.IsSuccessStatusCode)
        {
            dosing = JsonSerializer.Deserialize<DosingDoc>(await dosingResp.Content.ReadAsStringAsync(), Json.Options)!;
            EvalMetrics.ScoreDosing(dosing, report);
        }
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    {
        Console.WriteLine($"   dosing check SKIPPED (transport failure: {e.Message}) — matrix scores above still stand");
    }

    // Plan-5 Decision invariants, same contract: the harness signs no gates (neither regulatory nor VP),
    // so most cases never mint a DecisionDoc — a 404 here is expected and scores nothing. When one DOES
    // exist, a breach in it is a harm case (a signed nonexistent code, an order outside the signature, a
    // signature over an uncleared row) and counts as a FALSE PASS, tripping the non-zero exit. Same
    // transport guard, same reason: an optional read must not cost us the report, and a printed skip keeps
    // "decision unchecked" from ever looking like "decision checked clean".
    try
    {
        var decisionResp = await http.GetAsync($"projects/{projectId}/decision");
        if (decisionResp.IsSuccessStatusCode)
        {
            var decision = JsonSerializer.Deserialize<DecisionDoc>(await decisionResp.Content.ReadAsStringAsync(), Json.Options)!;
            if (dosing is not null)
                EvalMetrics.ScoreDecision(decision, dosing, report);
            else
                Console.WriteLine("   decision check SKIPPED (no DosingDoc fetched to validate the signed codes against)");
        }
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    {
        Console.WriteLine($"   decision check SKIPPED (transport failure: {e.Message}) — scores above still stand");
    }

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
