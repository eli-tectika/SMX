using System.Reflection;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class NoParkStatusesTests
{
    /// The park family is a COMPILE-TIME fact, not a review item — the same discipline that made the
    /// frontend's PARKED map a Record over a union rather than a switch somebody has to remember to extend.
    ///
    /// Reintroducing a park means deleting this test, which is a conversation. Adding one quietly is not
    /// possible, which is the point: four of these existed for months after execution-core §8 said they
    /// should not, because nothing failed when they stayed.
    [Fact]
    public void StageStatus_DeclaresNoParkConstants()
    {
        var values = typeof(StageStatus)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.DoesNotContain(values, v => v.StartsWith("awaiting-RE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(values, v => v.StartsWith("awaiting-physics", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(values, v => v.StartsWith("awaiting-operator", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(values, v => v.StartsWith("awaiting-VP", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(values, v => v.StartsWith("awaiting-samples", StringComparison.OrdinalIgnoreCase));
    }

    /// The five states a stage can be in, and there are no others. `awaiting-confirmation` went too: what
    /// it meant — the operator has not authorised the analysis — is a PROJECT-level fact
    /// (ProjectDoc.AnalysisStartedAt), not something a stage's status should have been carrying.
    ///
    /// Pinned as a set so a sixth arriving quietly is a failure here rather than a surprise in a UI fold
    /// months later.
    [Fact]
    public void StageStatus_IsTheExpectedSet()
    {
        var values = typeof(StageStatus)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order()
            .ToArray();

        Assert.Equal(new[] { "done", "failed", "needs-review", "pending", "running" }, values);
    }
}
