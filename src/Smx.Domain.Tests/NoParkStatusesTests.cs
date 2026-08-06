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

    /// The four states a stage can actually be in, plus the one remaining `awaiting-confirmation` that
    /// belongs to project CREATION rather than to the pipeline (it is deleted separately, with the
    /// intake-at-creation change). Pinned as a set so a fifth arriving quietly is a failure here rather
    /// than a surprise in a UI fold months later.
    [Fact]
    public void StageStatus_IsTheExpectedSet()
    {
        var values = typeof(StageStatus)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order()
            .ToArray();

        Assert.Equal(
            new[] { "awaiting-confirmation", "done", "failed", "needs-review", "pending", "running" },
            values);
    }
}
