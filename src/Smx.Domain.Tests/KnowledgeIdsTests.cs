using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RevisionConclusionIdTests
{
    [Fact]
    public void IsKeyedByTheDecision_SoRedeliveryUpserts_ButASecondRevisionAccumulates()
    {
        var first = RecordIds.Revision("proj-1", Stages.Discovery, "aaaa1111");
        var second = RecordIds.Revision("proj-1", Stages.Discovery, "bbbb2222");

        // Same revision twice (change-feed redelivery) → same conclusion id → an upsert, not a duplicate.
        Assert.Equal(
            KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, first),
            KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, first));

        // A second, different revision on the same scope → a NEW conclusion. Design §6.1 is explicit that
        // conclusions ACCUMULATE ("later findings refine earlier ones"), so this must not collide.
        Assert.NotEqual(
            KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, first),
            KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, second));
    }

    [Fact]
    public void CarriesTheKindAsThePartitionKeyPrefix() =>
        Assert.StartsWith($"{KnowledgeKinds.Material}|",
            KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, "proj-1|revision|discovery|aaaa1111"));
}
