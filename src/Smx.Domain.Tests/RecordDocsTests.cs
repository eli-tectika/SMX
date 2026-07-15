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
    public void ConstraintsDoc_CarriesTheMeasuredPhysicsInputs_TheFloorIsComputedFrom()
    {
        var c = new ConstraintsDoc
        {
            Id = RecordIds.Constraints("proj-1"), ProjectId = "proj-1",
            Components = [new ComponentSpec("bottle", "HDPE", "packaging", ["EU"], "brand", 250.0)],
            MeasuredBackgrounds = [new MeasuredBackground("bottle", "Zr", 4.0, "ppm")],
            Device = new XrfDevice("Olympus Vanta M", [new DeviceLod("Zr", 1.5, "ppm")]),
        };
        var json = JsonSerializer.Serialize(c, Json.Options);
        var back = JsonSerializer.Deserialize<ConstraintsDoc>(json, Json.Options)!;

        Assert.Equal(4.0, Assert.Single(back.MeasuredBackgrounds).Level);
        Assert.Equal("ppm", Assert.Single(back.MeasuredBackgrounds).Unit);
        Assert.Equal(1.5, Assert.Single(back.Device!.Lods).Lod);
        Assert.Equal(250.0, Assert.Single(back.Components).BatchMassKg);
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
        Assert.Null(v.ProposedDetermination);
        Assert.Null(v.ProposedReason);

        v.EvidenceReviewed = true;
        v.Determination = Determinations.Rejected;
        v.DeterminationReason = "EU Cosmetics Annex III";
        // The agent's proposal round-trips as a SEPARATE field. Pinned because it is invisible when it
        // breaks: a [JsonIgnore] or a Cosmos member-name quirk would silently delete the whole proposal
        // feature — the operator would see an empty field and go back to authoring every determination by
        // hand, with a green test suite the entire time.
        v.ProposedDetermination = Determinations.Recommended;
        v.ProposedReason = "clean on all four dimensions";

        var back = System.Text.Json.JsonSerializer.Deserialize<VerdictDoc>(
            System.Text.Json.JsonSerializer.Serialize(v, Json.Options), Json.Options)!;
        Assert.True(back.EvidenceReviewed);
        Assert.Equal(Determinations.Rejected, back.Determination);
        Assert.Equal("EU Cosmetics Annex III", back.DeterminationReason);
        Assert.Equal(Determinations.Recommended, back.ProposedDetermination);
        Assert.Equal("clean on all four dimensions", back.ProposedReason);
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

    [Fact]
    public void DosingDoc_RoundTrips_WithTypeDiscriminatorOnTheWire_AndAWindow()
    {
        var doc = new DosingDoc
        {
            Id = RecordIds.Dosing("p1"), ProjectId = "p1",
            Windows = [new PpmWindow("bottle", "1314-36-9", "Y",
                Floor: new Bound(12.0, "3-sigma over measured background", BoundKinds.Measured, 1.0),
                Upper: new Bound(1200.0, "no regulatory cap; solubility estimate", BoundKinds.Estimate, 0.4),
                RecommendedPpm: 600.0, QuantificationPpm: 40.0)],
            ReviewNote = "PL + VP + physics reviewed", ReviewedAt = "2026-07-15T00:00:00Z",
            GeneratedAt = "2026-07-15T00:00:00Z",
        };
        var json = JsonSerializer.Serialize(doc, Json.Options);
        // The change feed routes on this string and nothing else (RecordDocRouter). Lose it and the change
        // feed silently stops dispatching the Dosing stage. Assert it on the wire, not on the object.
        Assert.Contains("\"type\":\"dosing\"", json);
        Assert.Contains("\"projectId\":\"p1\"", json);

        var back = JsonSerializer.Deserialize<DosingDoc>(json, Json.Options)!;
        Assert.Equal("p1|dosing", back.Id);
        var w = Assert.Single(back.Windows);
        // The floor is MEASURED (confidence 1.0), the upper an ESTIMATE (0.4) — the two ends are not equally
        // trustworthy, and both the kind and the confidence have to survive for the UI to say which is which.
        Assert.Equal(BoundKinds.Measured, w.Floor.Kind);
        Assert.Equal(1.0, w.Floor.Confidence);
        Assert.Equal(BoundKinds.Estimate, w.Upper.Kind);
        Assert.Equal(600.0, w.RecommendedPpm);
        Assert.Equal(40.0, w.QuantificationPpm);
        Assert.Equal("PL + VP + physics reviewed", back.ReviewNote);
    }

    /// The whole reason <see cref="MarkerCode.RatioSignature"/> is a DERIVED get-only property and not a stored
    /// field: it is serialized OUT (Cosmos, the API and the UI all still see `ratioSignature`) but IGNORED on
    /// read and recomputed from the markers. This pins both halves — and it is written to FAIL if the property
    /// were ever demoted to a plain stored `{ get; set; }`, because then the tampered value below would be read
    /// straight back in and the code's stated identity would no longer be its actual ratio.
    [Fact]
    public void DosingDoc_RatioSignature_IsOnTheWire_ButRecomputedOnRead_NotReadBack()
    {
        var doc = new DosingDoc
        {
            Id = RecordIds.Dosing("p1"), ProjectId = "p1", GeneratedAt = "2026-07-15T00:00:00Z",
            Codes = [new MarkerCode("bottle",
                [new CodeMarker("1314-36-9", "Y", 600.0, 0.787, 472.2, 600.0),
                 new CodeMarker("10035-04-8", "Zr", 300.0, 0.74, 222.0, 300.0)],
                "two-marker code")],
        };
        var json = JsonSerializer.Serialize(doc, Json.Options);
        // Y at 600 and Zr at 300 -> ratio 1.00:0.50, elements ordered by descending ppm. The signature is on
        // the wire in the WRITE direction, computed from the markers.
        Assert.Contains("\"ratioSignature\":\"Y:Zr = 1.00:0.50\"", json);

        // Now corrupt ONLY the persisted signature string, as a stale document (Law 8: the operator revises a
        // code's ppm through the agent, which rewrites `markers`; a stored signature could be left behind) or a
        // tampered one would be. The markers are untouched.
        var tampered = json.Replace("Y:Zr = 1.00:0.50", "Zr:Y = 9.99:9.99");
        Assert.Contains("Zr:Y = 9.99:9.99", tampered);   // the tamper actually took — the test is not vacuous

        var back = JsonSerializer.Deserialize<DosingDoc>(tampered, Json.Options)!;
        // The identity read back is the one RECOMPUTED from the markers, NOT the tampered string. A field
        // reader matches a suspect sample against this; deriving it makes a code whose stated identity is not
        // its actual ratio unrepresentable. A stored `{ get; set; }` would return "Zr:Y = 9.99:9.99" here.
        Assert.Equal("Y:Zr = 1.00:0.50", back.Codes[0].RatioSignature);
    }

    [Fact]
    public void CostDoc_RoundTrips_WithTypeDiscriminator_AndPreservesNestedCitationsAndNullQuotes()
    {
        var doc = new CostDoc
        {
            Id = RecordIds.Cost("p1"), ProjectId = "p1", GeneratedAt = "2026-07-15T00:00:00Z",
            Substances =
            [
                new SupplierAudit("1314-36-9", "Y", ["Acme", "Sigma"],
                    BestQuote: new PriceQuote(4.20, "USD", "Acme", "100 g",
                        new Citation("catalog", "ref-catalog/product|Y|oxide", "2026-07-15T00:00:00Z", "$420 / 100 g")),
                    PriceNote: "best of 2 parseable listings",
                    Risks: []),
                new SupplierAudit("10035-04-8", "Zr", ["OnlySource"],
                    BestQuote: null,
                    PriceNote: "price is free text on the only listing; nothing parseable on file",
                    Risks: ["single-source", "not-off-the-shelf"]),
            ],
        };
        var json = JsonSerializer.Serialize(doc, Json.Options);
        // RecordDocRouter switches on this and nothing else. Plan 5's Decision stage triggers off CostDoc, so
        // a wrong discriminator silently strands the whole downstream chain.
        Assert.Contains("\"type\":\"cost\"", json);
        Assert.Contains("\"projectId\":\"p1\"", json);
        // The Citation is three levels deep (SupplierAudit -> PriceQuote -> Citation). Assert its reference
        // reaches the wire: a [JsonIgnore] or a required-member mismatch would silently drop the one thing that
        // makes a price checkable, and procurement acts on these numbers.
        Assert.Contains("ref-catalog/product|Y|oxide", json);

        var back = JsonSerializer.Deserialize<CostDoc>(json, Json.Options)!;
        Assert.Equal("p1|cost", back.Id);
        Assert.Equal(RecordTypes.Cost, back.Type);
        Assert.Equal(2, back.Substances.Count);

        var quoted = back.Substances[0];
        Assert.NotNull(quoted.BestQuote);
        Assert.Equal(4.20, quoted.BestQuote!.UsdPerGram);
        Assert.Equal("Acme", quoted.BestQuote.Supplier);
        // The nested Citation survived the round trip, all three levels down.
        Assert.Equal("catalog", quoted.BestQuote.Citation.Source);
        Assert.Equal("ref-catalog/product|Y|oxide", quoted.BestQuote.Citation.Reference);
        Assert.Equal("$420 / 100 g", quoted.BestQuote.Citation.Snippet);
        Assert.Equal(["Acme", "Sigma"], quoted.Suppliers);

        var unquoted = back.Substances[1];
        // A null BestQuote must survive AS null — nothing is ever interpolated into existence, and a Cost stage
        // that read back a fabricated zero-dollar quote would be inventing the number procurement acts on.
        Assert.Null(unquoted.BestQuote);
        Assert.Equal("price is free text on the only listing; nothing parseable on file", unquoted.PriceNote);
        Assert.Equal(["single-source", "not-off-the-shelf"], unquoted.Risks);
    }

    [Fact]
    public void SubstanceProperty_RoundTrips_AndIsKeyedByCasSoItIsReusedAcrossProjects()
    {
        var doc = new SubstancePropertyDoc
        {
            Id = KnowledgeIds.SubstanceProperty("1314-36-9"), Cas = "1314-36-9",
            Element = "Y", Form = "oxide", MetalLoading = 0.787,
            Basis = "Y2O3, M(Y)=88.906, M(Y2O3)=225.81 → 2×88.906/225.81",
            EnteredAt = "2026-07-14T10:00:00.0000000+00:00",
        };
        var json = JsonSerializer.Serialize(doc, Json.Options);
        Assert.Contains("\"type\":\"substance-property\"", json);
        Assert.Equal("substance-property|1314-36-9", doc.Id);

        // The `/cas` Cosmos partition-key path is a string declared in Bicep; this is the C# half of that
        // contract. If the serialized key is not exactly `cas`, Cosmos cannot extract the partition key from
        // the document and rejects EVERY upsert — a failure no fake-backed test can see.
        Assert.Contains("\"cas\":\"1314-36-9\"", json);

        var back = JsonSerializer.Deserialize<SubstancePropertyDoc>(json, Json.Options)!;
        Assert.Equal(0.787, back.MetalLoading);
        // Basis is what makes the number checkable; a loading that survives the round-trip without its
        // provenance is an unauditable number on a purchase order.
        Assert.Equal(doc.Basis, back.Basis);
    }
}
