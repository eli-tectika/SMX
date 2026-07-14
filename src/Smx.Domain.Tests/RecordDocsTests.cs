using System.Text.Json;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RecordDocsTests
{
    [Fact]
    public void VerdictId_IsDeterministic_AndPipeDelimited()
    {
        Assert.Equal("p1|verdict|39049-04-2|bottle", RecordIds.Verdict("p1", "39049-04-2", "bottle"));
        Assert.Equal("p1|constraints", RecordIds.Constraints("p1"));
        Assert.Equal("p1|matrix", RecordIds.Matrix("p1"));
    }

    [Fact]
    public void ProjectDoc_SerializesCamelCase_WithTypeDiscriminator()
    {
        var doc = ProjectDoc.Create("p1", "Acme", "Shampoo bottle", JsonDocument.Parse("{}").RootElement);
        var json = JsonSerializer.Serialize(doc, Json.Options);
        Assert.Contains("\"type\":\"project\"", json);
        Assert.Contains("\"projectId\":\"p1\"", json);
        Assert.Contains("\"intake\"", json); // stages seeded
        var back = JsonSerializer.Deserialize<ProjectDoc>(json, Json.Options)!;
        Assert.Equal("pending", back.Stages["intake"].Status);
    }

    /// THE TRIPWIRE FOR PLAN 4. `ProjectDoc.Create`'s stage dictionary is a fourth, hand-maintained
    /// enumeration of the stages, and it was the only one nothing pinned. The other three are safe: Stages.All
    /// is reflection-tested against the constants (ChatEndpointsTests), and ToolBox.ReadToolsFor and
    /// StageDispatcher.StageInputsJsonAsync both fail CLOSED on a stage they do not know.
    ///
    /// This one fails OPEN, in both directions. Add `Stages.Dosing` and forget this dictionary and:
    ///   - Stages.All gains it the same commit (the reflection test forces that), so POST /stages/dosing/chat
    ///     starts accepting messages immediately — and the dispatcher runs the turn with no tools over "{}"
    ///     inputs: a confident conversation about nothing;
    ///   - SetStageAsync(projectId, "dosing", …) does `p.Stages[stage]` and throws KeyNotFoundException on
    ///     every project created before the change.
    /// Neither shows up as a compile error. This assertion is what shows up instead.
    [Fact]
    public void ProjectDoc_Create_SeedsExactlyTheStagesInStagesAll()
    {
        var doc = ProjectDoc.Create("p1", "Acme", "Shampoo bottle", JsonDocument.Parse("{}").RootElement);
        Assert.Equal([.. Stages.All.Order()], [.. doc.Stages.Keys.Order()]);
    }

    [Theory]
    [InlineData(new[] { "Pass", "Pass" }, "Pass")]
    [InlineData(new[] { "Pass", "Conditional" }, "Conditional")]
    [InlineData(new[] { "Conditional", "NeedsReview" }, "NeedsReview")]
    [InlineData(new[] { "NeedsReview", "Fail" }, "Fail")]
    public void Verdict_Overall_IsWorstOfDimensions(string[] statuses, string expected)
    {
        var dims = statuses.Select((s, i) => new DimensionVerdict(
            Dimension: ((VerdictDimension)i).ToString(),
            Status: Enum.Parse<VerdictStatus>(s),
            Citations: [new Citation("reg-index", "doc-1#chunk-3", "2026-07-08T00:00:00Z")],
            Confidence: 0.9,
            Rationale: "r")).ToList();
        Assert.Equal(Enum.Parse<VerdictStatus>(expected), VerdictDoc.Fold(dims));
    }

    [Fact]
    public void Verdict_RoundTrips()
    {
        var v = new VerdictDoc
        {
            Id = RecordIds.Verdict("p1", "c1", "bottle"),
            ProjectId = "p1", Cas = "c1", ComponentId = "bottle", Element = "Zr", Form = "neodecanoate",
            Dimensions = [new DimensionVerdict("ElementGate", VerdictStatus.Pass,
                [new Citation("reg-index", "reach-annex17#e23", "2026-07-08T00:00:00Z")], 0.95, "not listed")],
        };
        var back = JsonSerializer.Deserialize<VerdictDoc>(JsonSerializer.Serialize(v, Json.Options), Json.Options)!;
        Assert.Equal(VerdictStatus.Pass, back.Overall);
        Assert.Single(back.Dimensions[0].Citations);
    }

    [Fact]
    public void CandidatesDoc_HasDeterministicId_AndCandidatesType()
    {
        var doc = new CandidatesDoc
        {
            Id = RecordIds.Candidates("p1"), ProjectId = "p1",
            Substances = [new("bottle", "Y", "2-ethylhexanoate", "136-25-4", "sub-micron", "mineral spirits", true, "A", "clean XRF, catalog-available", [new Citation("catalog", "ref-catalog/product|Y|x", "t")])],
        };
        Assert.Equal("p1|candidates", doc.Id);
        Assert.Equal(RecordTypes.Candidates, doc.Type);
        Assert.Equal("A", doc.Substances[0].Tier);
        Assert.True(doc.Substances[0].Preferred);
    }

    [Fact]
    public void ElementPool_CarriesComponentAndSignalNote()
    {
        var pool = new ElementPool("liquid", "Sc", "Kα", "L", "small-amount peak");
        Assert.Equal("liquid", pool.Component);
        Assert.Equal("L", pool.Status);
        Assert.Equal("small-amount peak", pool.SignalNote);
    }

    [Fact]
    public void ProjectCreate_SeedsIntakeDiscoveryRegulatoryMatrix()
    {
        var p = ProjectDoc.Create("p1", "Acme", "P", System.Text.Json.JsonDocument.Parse("{}").RootElement);
        Assert.True(p.Stages.ContainsKey(Stages.Intake));
        Assert.True(p.Stages.ContainsKey(Stages.Discovery));
        Assert.True(p.Stages.ContainsKey(Stages.Regulatory));
        Assert.True(p.Stages.ContainsKey(Stages.Matrix));
        Assert.False(p.Stages.ContainsKey("screening"));
        Assert.Equal(4, p.Stages.Count);
    }

    [Fact]
    public void ConstraintsDoc_CarriesElementPools_AndProvidedCandidates()
    {
        var c = new ConstraintsDoc
        {
            Id = RecordIds.Constraints("p1"), ProjectId = "p1",
            Components = [new("bottle", "PET", "packaging", ["EU"], "brand")],
            ElementPools = [new("bottle", "Y", "Kα", "V", null)],
            ProvidedCandidates = [new("bottle", "Y", "2-EH", "136-25-4", null, null, true, "A", "provided", [])],
            ClientRestrictedList = ["Pb"],
            DerivedScope = [new("reach-annex-xvii", "*", "gate", new Citation("regulatory", "x", "t"))],
        };
        Assert.Single(c.ElementPools);
        Assert.Single(c.ProvidedCandidates);
        Assert.Equal("V", c.ElementPools[0].Status);
    }

    [Fact]
    public void VerdictDoc_CarriesOperatorReviewFields_DefaultingUnset()
    {
        var v = new VerdictDoc
        {
            Id = RecordIds.Verdict("p1", "c1", "bottle"),
            ProjectId = "p1", Cas = "c1", ComponentId = "bottle", Element = "Zr", Form = "neodec",
        };
        Assert.False(v.EvidenceReviewed);
        Assert.Null(v.Determination);
        Assert.Null(v.DeterminationReason);

        v.EvidenceReviewed = true;
        v.Determination = "rejected";
        v.DeterminationReason = "EU Cosmetics Annex III";
        var back = System.Text.Json.JsonSerializer.Deserialize<VerdictDoc>(
            System.Text.Json.JsonSerializer.Serialize(v, Json.Options), Json.Options)!;
        Assert.True(back.EvidenceReviewed);
        Assert.Equal("rejected", back.Determination);
        Assert.Equal("EU Cosmetics Annex III", back.DeterminationReason);
    }

    [Fact]
    public void GateDoc_HasDeterministicId_TypeAndDefaults()
    {
        var g = new GateDoc { Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1", GateType = GateTypes.Regulatory };
        Assert.Equal("p1|gate|regulatory", g.Id);
        Assert.Equal(RecordTypes.Gate, g.Type);
        Assert.Equal("regulatory", g.GateType);
        Assert.Equal("locked", g.Status);
    }

    [Fact]
    public void RevisionId_IsScopedToProjectAndStage()
    {
        Assert.Equal("proj-1|revision|discovery|a1b2c3d4",
            RecordIds.Revision("proj-1", Stages.Discovery, "a1b2c3d4"));
    }

    [Fact]
    public void RevisionDoc_RoundTrips_WithTypeDiscriminatorOnTheWire()
    {
        var r = new RevisionDoc
        {
            Id = RecordIds.Revision("proj-1", Stages.Discovery, "a1b2c3d4"), ProjectId = "proj-1",
            Stage = Stages.Discovery, Target = "Ba tier", Reason = "overlaps the Ti K-beta line",
            CreatedAt = "2026-07-13T00:00:00Z",
        };
        var json = JsonSerializer.Serialize(r, Json.Options);
        // "type" is the sole key RecordDocRouter.Route switches on: lose it and the change feed
        // silently stops dispatching revisions. Assert it on the wire, not on the object.
        Assert.Contains("\"type\":\"revision\"", json);
        Assert.Contains("\"projectId\":\"proj-1\"", json);
        Assert.Contains("\"status\":\"pending\"", json);

        var back = JsonSerializer.Deserialize<RevisionDoc>(json, Json.Options)!;
        Assert.Equal("proj-1|revision|discovery|a1b2c3d4", back.Id);
        Assert.Equal(Stages.Discovery, back.Stage);
        Assert.Equal("Ba tier", back.Target);
        Assert.Equal("overlaps the Ti K-beta line", back.Reason);
        Assert.Equal("2026-07-13T00:00:00Z", back.CreatedAt);
        Assert.Equal(RevisionStatus.Pending, back.Status);
        // Json.Options omits nulls when writing; they must come back null, not "".
        Assert.Null(back.Cas);
        Assert.Null(back.ComponentId);
        Assert.Null(back.ConclusionId);
        Assert.Null(back.AppliedAt);
        Assert.Null(back.Error);
    }

    [Fact]
    public void ChatDocs_RoundTrip_WithTheirTypeDiscriminatorsOnTheWire()
    {
        var msg = new ChatMessageDoc
        {
            Id = RecordIds.ChatMessage("proj-1", Stages.Discovery, "aaaa1111"), ProjectId = "proj-1",
            Stage = Stages.Discovery, Text = "why is Ba tier A?", CreatedAt = "2026-07-13T10:00:00Z",
        };
        var json = JsonSerializer.Serialize(msg, Json.Options);
        // The change feed routes on this string and nothing else (RecordDocRouter).
        Assert.Contains("\"type\":\"chat-message\"", json);
        // `status` must reach the wire: the change-feed idempotency guard reads it back from Cosmos, and a
        // status that silently didn't serialize would let a redelivered message re-run its agent.
        Assert.Contains("\"status\":\"pending\"", json);
        Assert.Equal(ChatStatus.Pending, JsonSerializer.Deserialize<ChatMessageDoc>(json, Json.Options)!.Status);

        var reply = new ChatReplyDoc
        {
            Id = RecordIds.ChatReply("proj-1", Stages.Discovery, "aaaa1111"), ProjectId = "proj-1",
            Stage = Stages.Discovery, MessageId = msg.Id, Text = "Because the catalog lists it clean.",
            ToolCalls = [new ChatToolCall("search_catalog", "element=Ba", null)],
            CreatedAt = "2026-07-13T10:00:05Z",
        };
        var replyJson = JsonSerializer.Serialize(reply, Json.Options);
        Assert.Contains("\"type\":\"chat-reply\"", replyJson);
        var back = JsonSerializer.Deserialize<ChatReplyDoc>(replyJson, Json.Options)!;
        Assert.Equal(msg.Id, back.MessageId);
        Assert.Equal("search_catalog", Assert.Single(back.ToolCalls).Tool);
    }

    [Fact]
    public void ChatIds_PairAReplyToItsMessage()
    {
        // A reply's id is derived from its message's key, so a redelivered chat-message cannot produce a
        // second reply doc — it upserts the same one.
        Assert.Equal("proj-1|chat-message|discovery|aaaa1111", RecordIds.ChatMessage("proj-1", Stages.Discovery, "aaaa1111"));
        Assert.Equal("proj-1|chat-reply|discovery|aaaa1111", RecordIds.ChatReply("proj-1", Stages.Discovery, "aaaa1111"));
    }
}
