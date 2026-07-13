using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class RevisionEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public RevisionEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();
    }

    private async Task SeedProject(string pid) =>
        await _store.UpsertProjectAsync(ProjectDoc.Create(pid, "Acme", "P", JsonDocument.Parse("{}").RootElement));

    private async Task SeedCandidates(string pid, string cas = "cas1")
    {
        await SeedProject(pid);
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(pid), ProjectId = pid,
            Substances = [new CandidateSubstance("bottle", "Zr", "neodec", cas, null, null, false, "A", "seed", [])],
        });
    }

    private async Task SeedVerdict(string pid, string cas = "cas1")
    {
        await SeedCandidates(pid, cas);
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(pid, cas, "bottle"), ProjectId = pid, Cas = cas, ComponentId = "bottle",
            Element = "Zr", Form = "neodec",
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("r", "x", "t")], 0.9, "r")],
        });
    }

    private IReadOnlyList<RevisionDoc> Revisions(string pid) =>
        _store.Documents.OfType<RevisionDoc>().Where(r => r.ProjectId == pid).ToList();

    [Fact]
    public async Task Revise_Discovery_QueuesPendingRevisionOnTheBus()
    {
        await SeedCandidates("p1");
        var resp = await _client.PostAsJsonAsync("/projects/p1/stages/discovery/revise",
            new { target = "drop the Zr neodecanoate candidate", reason = "supplier discontinued the grade" });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("revisionId").GetString()));
        Assert.Equal(RevisionStatus.Pending, body.GetProperty("status").GetString());

        var doc = Assert.Single(Revisions("p1"));
        Assert.Equal(Stages.Discovery, doc.Stage);
        Assert.Equal("supplier discontinued the grade", doc.Reason);
        Assert.Equal("drop the Zr neodecanoate candidate", doc.Target);
        Assert.Equal(RevisionStatus.Pending, doc.Status);
        Assert.False(string.IsNullOrWhiteSpace(doc.CreatedAt));
        // The audit trail is ordered by a LEXICOGRAPHIC sort on CreatedAt — round-trippable "O" only.
        Assert.True(DateTimeOffset.TryParse(doc.CreatedAt, out _));
        Assert.Contains('.', doc.CreatedAt); // fixed-width "O", not a whole-second "…Z"
    }

    [Fact]
    public async Task Revise_WithoutReason_Returns422_AndWritesNothing()
    {
        // Law 4: a revision without a reason is a silent edit that teaches the system nothing.
        await SeedCandidates("p1");
        var resp = await _client.PostAsJsonAsync("/projects/p1/stages/discovery/revise",
            new { target = "drop the candidate", reason = "  " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("reason", await resp.Content.ReadAsStringAsync());
        Assert.Empty(Revisions("p1"));
    }

    [Fact]
    public async Task Revise_WithoutTarget_Returns422()
    {
        await SeedCandidates("p1");
        var resp = await _client.PostAsJsonAsync("/projects/p1/stages/discovery/revise",
            new { target = "", reason = "supplier discontinued the grade" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Empty(Revisions("p1"));
    }

    [Fact]
    public async Task Revise_NonRevisableStage_Returns422()
    {
        await SeedCandidates("p1");
        var resp = await _client.PostAsJsonAsync("/projects/p1/stages/matrix/revise",
            new { target = "recolor the C cells", reason = "they read as fails" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("cannot be revised", await resp.Content.ReadAsStringAsync());
        Assert.Empty(Revisions("p1"));
    }

    [Fact]
    public async Task Revise_MissingReason_BeatsUnknownProject_Returns422()
    {
        // The reason check is cheap and runs before any store lookup: a missing reason is ALWAYS a 422.
        var resp = await _client.PostAsJsonAsync("/projects/ghost/stages/discovery/revise",
            new { target = "anything", reason = "" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Revise_UnknownProject_Returns404()
    {
        var resp = await _client.PostAsJsonAsync("/projects/ghost/stages/discovery/revise",
            new { target = "drop the candidate", reason = "supplier discontinued the grade" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Revise_Discovery_BeforeCandidatesExist_Returns422()
    {
        await SeedProject("p1"); // no CandidatesDoc yet
        var resp = await _client.PostAsJsonAsync("/projects/p1/stages/discovery/revise",
            new { target = "drop the candidate", reason = "supplier discontinued the grade" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("nothing to revise", await resp.Content.ReadAsStringAsync());
        Assert.Empty(Revisions("p1"));
    }

    [Fact]
    public async Task Revise_Regulatory_WithoutCasAndComponentId_Returns422()
    {
        // A verdict is per substance × component — the dispatcher must never guess which one was meant.
        await SeedVerdict("p1");
        var resp = await _client.PostAsJsonAsync("/projects/p1/stages/regulatory/revise",
            new { target = "re-screen it", reason = "REACH Annex XVII entry 63 was updated" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("componentId", await resp.Content.ReadAsStringAsync());
        Assert.Empty(Revisions("p1"));
    }

    [Fact]
    public async Task Revise_Regulatory_WithoutComponentId_Returns422()
    {
        await SeedVerdict("p1");
        var resp = await _client.PostAsJsonAsync("/projects/p1/stages/regulatory/revise",
            new { target = "re-screen it", reason = "REACH update", cas = "cas1" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Empty(Revisions("p1"));
    }

    [Fact]
    public async Task Revise_Regulatory_UnknownVerdict_Returns422()
    {
        await SeedVerdict("p1");
        var resp = await _client.PostAsJsonAsync("/projects/p1/stages/regulatory/revise",
            new { target = "re-screen it", reason = "REACH update", cas = "nope", componentId = "bottle" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("no verdict", await resp.Content.ReadAsStringAsync());
        Assert.Empty(Revisions("p1"));
    }

    [Fact]
    public async Task Revise_Regulatory_QueuesRevisionNamingTheVerdict()
    {
        await SeedVerdict("p1");
        var resp = await _client.PostAsJsonAsync("/projects/p1/stages/regulatory/revise",
            new { target = "conditional, not fail", reason = "REACH Annex XVII entry 63 exempts this use", cas = "cas1", componentId = "bottle" });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var doc = Assert.Single(Revisions("p1"));
        Assert.Equal(Stages.Regulatory, doc.Stage);
        Assert.Equal("cas1", doc.Cas);
        Assert.Equal("bottle", doc.ComponentId);
        Assert.Equal(RevisionStatus.Pending, doc.Status);
    }

    [Fact]
    public async Task GetRevisions_EmptyOnColdStart_ThenReturnsTheTrail()
    {
        await SeedCandidates("p1");
        var cold = await _client.GetFromJsonAsync<JsonElement>("/projects/p1/revisions");
        Assert.Equal(JsonValueKind.Array, cold.ValueKind);
        Assert.Empty(cold.EnumerateArray());

        await _client.PostAsJsonAsync("/projects/p1/stages/discovery/revise",
            new { target = "drop Zr", reason = "supplier discontinued the grade" });
        await _client.PostAsJsonAsync("/projects/p1/stages/discovery/revise",
            new { target = "add Hf", reason = "physics XRF shows a clean window at Hf" });

        var trail = await _client.GetFromJsonAsync<JsonElement>("/projects/p1/revisions");
        Assert.Equal(2, trail.GetArrayLength());
        var reasons = trail.EnumerateArray().Select(r => r.GetProperty("reason").GetString()).ToList();
        Assert.Contains("supplier discontinued the grade", reasons);
        Assert.Contains("physics XRF shows a clean window at Hf", reasons);
        // two revisions of the same stage are two distinct decisions — both are in the trail
        Assert.Equal(2, trail.EnumerateArray().Select(r => r.GetProperty("id").GetString()).Distinct().Count());
    }
}
