using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;

namespace Smx.Backend.Tests;

/// THE SAME EFFECT BY TWO DOORS — asserted, not asserted ABOUT.
///
/// Revise-with-reason (Law 4) has two front doors: the operator's structured form
/// (POST /projects/{id}/stages/{stage}/revise) and the operator TELLING the agent, in chat, why something
/// should change (ChatTools.apply_revision). Both write ONE thing: a `pending` RevisionDoc on the bus. From
/// there a single dispatcher does everything that matters — it re-runs the stage, VOIDS the regulatory gate
/// the revision invalidates (Law 9: the operator's signature no longer covers the analysis), and records the
/// reason as a Learned Conclusion.
///
/// So the doc IS the contract. Let the two docs diverge in any field the dispatcher reads and one door keeps
/// its guarantees while the other quietly does not — and nobody would notice WHICH: both look like a
/// legitimate revision on the bus. A `stage` the dispatcher cannot revise, a missing `cas`, a `status` that
/// is not `pending` — each one silently costs the gate void, the re-run, or the conclusion.
///
/// This test lives HERE, in the backend's test project, and not beside the rest of Law 9 in
/// Smx.Orchestrator.Tests/ChatGuardrailTests.cs, for one reason: the HTTP door can only be driven from a
/// project that can host it (WebApplicationFactory), and only this test project targets a framework whose
/// TestHost runs on the installed runtime. Hitting the REAL endpoint — real routing, real JSON binding — is
/// the whole point of the test, so the test goes where the endpoint can run, and ChatGuardrailTests points here.
public class ChatRevisionParityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string P = "p1";
    private const string Cas = "cas-zr";
    private const string Component = "bottle";

    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public ChatRevisionParityTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();
    }

    /// A project with an analytical result to revise: candidates, and a verdict for the cell a REGULATORY
    /// revision has to name. Both doors run the same preconditions against this same record.
    private async Task SeedAsync()
    {
        await _store.UpsertProjectAsync(
            ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement));
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new CandidateSubstance(Component, "Zr", "neodecanoate", Cas, null, null, true, "A", "why", [])],
        });
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(P, Cas, Component), ProjectId = P, Cas = Cas, ComponentId = Component,
            Element = "Zr", Form = "neodecanoate",
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        });
    }

    /// The chat door, driven exactly as the MODEL drives it: through the real AIFunction and its generated
    /// JSON schema, never the C# method. (The schema can disagree with the signature — see ChatToolsTests.)
    private async Task ApplyRevisionInChatAsync(string stage, object args)
    {
        var tool = (AIFunction)new ChatTools(_store, P, stage, "m1").Tools().Single(t => t.Name == "apply_revision");
        await tool.InvokeAsync(new AIFunctionArguments(
            JsonSerializer.SerializeToNode(args)!.AsObject()
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value?.GetValue<string>())));
    }

    /// Everything about the doc EXCEPT the two fields that are legitimately per-write. `id` differs by
    /// construction (the endpoint mints a Guid; chat content-addresses it from the chat message, so that a
    /// redelivered turn converges instead of queueing the change twice), and `createdAt` is a clock reading.
    /// Every other field is contract — including any field ADDED to RevisionDoc later, which is exactly why
    /// this compares the whole serialized doc instead of a hand-written list of properties that a new field
    /// would silently slip past.
    private static JsonNode BehaviourOf(RevisionDoc doc)
    {
        var json = JsonSerializer.SerializeToNode(doc, Json.Options)!.AsObject();
        json.Remove("id");
        json.Remove("createdAt");
        return json;
    }

    [Theory]
    [InlineData(Stages.Discovery)]
    [InlineData(Stages.Regulatory)]
    public async Task AChatRevision_AndTheEndpointsRevision_AreTheSameDocInEveryFieldThatDrivesBehaviour(string stage)
    {
        await SeedAsync();
        // The identical instruction, said two ways: typed into the form, and said to the agent in chat.
        var target = "the Zr verdict on the bottle";
        var reason = "the R.E. says PPWR does not apply to a non-food HDPE bottle";
        var cas = stage == Stages.Regulatory ? Cas : null;
        var componentId = stage == Stages.Regulatory ? Component : null;

        await ApplyRevisionInChatAsync(stage, new { target, reason, cas, componentId });
        var chatDoc = Assert.Single(_store.Documents.OfType<RevisionDoc>());

        var resp = await _client.PostAsJsonAsync($"/projects/{P}/stages/{stage}/revise",
            new { target, reason, cas, componentId });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var endpointDoc = _store.Documents.OfType<RevisionDoc>().Single(r => r.Id != chatDoc.Id);

        // THE ASSERTION. Every field the dispatcher reads — stage, target, reason, cas, componentId, status,
        // type, and the applied/error/conclusion fields it keys idempotency on — is identical.
        Assert.Equal(BehaviourOf(endpointDoc).ToJsonString(), BehaviourOf(chatDoc).ToJsonString());

        // ...and the two fields that legitimately differ, differ for the reasons they are supposed to.
        Assert.NotEqual(endpointDoc.Id, chatDoc.Id);
        // Both timestamps are round-trippable "O" — the audit trail is ordered by a LEXICOGRAPHIC sort on
        // CreatedAt, so one door writing a whole-second "…Z" would silently misorder the trail against the
        // other's fixed-width "O" ('.' sorts before 'Z'). Same field, same format, or the order is a lie.
        foreach (var createdAt in new[] { endpointDoc.CreatedAt, chatDoc.CreatedAt })
        {
            Assert.True(DateTimeOffset.TryParse(createdAt, out _));
            Assert.Contains('.', createdAt);
        }
    }

    /// The preconditions must match too, or the doors are the same only on the happy path: a chat revision
    /// that the endpoint would have 422'd is a RevisionDoc the dispatcher can only fail — after the model has
    /// already told the operator the change is on its way. Both doors refuse, and both write NOTHING.
    [Theory]
    // no reason (Law 4: a change without a reason is a silent edit that teaches the system nothing)
    [InlineData(Stages.Discovery, "drop the Zr candidate", "  ", null, null)]
    // no target
    [InlineData(Stages.Discovery, " ", "it overlaps the Ti K-beta line", null, null)]
    // a regulatory revision that does not name the verdict to re-run (a verdict is per substance × component)
    [InlineData(Stages.Regulatory, "the verdict", "the R.E. disagrees", null, null)]
    // ...or names one that does not exist
    [InlineData(Stages.Regulatory, "the verdict", "the R.E. disagrees", "cas-nobody", Component)]
    public async Task BothDoors_RefuseTheSameRevisions_AndWriteNothing(
        string stage, string target, string reason, string? cas, string? componentId)
    {
        await SeedAsync();

        await ApplyRevisionInChatAsync(stage, new { target, reason, cas, componentId });
        Assert.Empty(_store.Documents.OfType<RevisionDoc>());

        var resp = await _client.PostAsJsonAsync($"/projects/{P}/stages/{stage}/revise",
            new { target, reason, cas, componentId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Empty(_store.Documents.OfType<RevisionDoc>());
    }
}
