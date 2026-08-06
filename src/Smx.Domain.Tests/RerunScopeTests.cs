using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RerunScopeTests
{
    /// THE DRIFT GUARD, and the reason this file exists.
    ///
    /// The allow-list says what an operator may change; this map says what changing it invalidates. They are
    /// two views of one list, maintained by hand in two files. A field in the allow-list with no declared
    /// scope would amend the record and re-run NOTHING — the project would go on displaying an analysis
    /// computed from a requirement that no longer holds, and nothing anywhere would error.
    ///
    /// It has already happened once in this codebase's history, to `batchMassKg`: added to the allow-list,
    /// missed in the tool description, and a comment saying "keep these in step" is what failed to stop it.
    /// This assertion is what stops it instead.
    [Fact]
    public void EveryAmendableField_DeclaresARerunScope_AndViceVersa()
    {
        var amendable = new HashSet<string>(IntakeAnswers.ComponentFields, StringComparer.Ordinal)
        {
            "clientRestrictedList",
        };

        Assert.Equal([.. amendable.Order()], [.. RerunScope.Map.Keys.Order()]);
    }

    [Fact]
    public void For_ResolvesAComponentPathToItsLeaf()
    {
        // The allow-list addresses component fields as `components.{id}.{field}`, and the blast radius of
        // `markets` does not depend on WHICH component's markets changed.
        Assert.Equal(RerunScope.For("markets"), RerunScope.For("components.bottle.markets"));
    }

    [Fact]
    public void For_ThrowsOnAnUndeclaredField_RatherThanReturningNothing()
    {
        // Empty would read as "this change invalidates nothing" -- the single most dangerous answer
        // available, because it is indistinguishable from a correct one until someone ships a marker.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => RerunScope.For("colour"));
        Assert.Contains("no rerun scope is declared", ex.Message);
    }

    [Fact]
    public void BatchMass_TouchesOnlyDosingAndTheDecisionOverIt()
    {
        // The cheapest rerun, and the case that proves the map earns its keep. A batch mass scales the ORDER
        // AMOUNT; it moves no chemistry, no verdict and no ppm window. Re-running Regulatory for it would
        // void the R.E.'s signature over an analysis that did not change.
        var scope = RerunScope.For("components.bottle.batchMassKg");

        Assert.Equal([Stages.Dosing, Stages.Decision], scope);
        Assert.DoesNotContain(Stages.Regulatory, scope);
        Assert.DoesNotContain(Stages.Discovery, scope);
    }

    [Fact]
    public void Markets_TouchesTheRegulatoryLane_ButNotTheChemistry()
    {
        // Target markets are an input to the application check. Discovery's candidates do not depend on them,
        // so re-running Discovery would be minutes of Foundry time for an identical answer.
        var scope = RerunScope.For("components.bottle.markets");

        Assert.Contains(Stages.Regulatory, scope);
        Assert.Contains(Stages.Decision, scope);
        Assert.DoesNotContain(Stages.Discovery, scope);
        Assert.DoesNotContain(Stages.Pool, scope);
    }

    [Fact]
    public void TheRegulatoryLane_AlwaysCarriesMatrixWithIt()
    {
        // Matrix is ASSEMBLED from the verdicts. Re-running Regulatory without it leaves a matrix that looks
        // current and describes verdicts that no longer exist -- the "never leave an artifact that is wrong
        // but looks current" rule.
        foreach (var field in new[] { "markets", "application", "clientRestrictedList" })
            Assert.Contains(Stages.Matrix, RerunScope.For(field));
    }

    [Fact]
    public void Material_And_Objective_InvalidateEverything_FromThePoolDown()
    {
        // The polymer and the objective decide which chemistry is a candidate at all, so there is no earlier
        // stage worth preserving.
        foreach (var field in new[] { "material", "objective" })
        {
            var scope = RerunScope.For(field);
            Assert.Contains(Stages.Pool, scope);
            Assert.Contains(Stages.Discovery, scope);
            Assert.Contains(Stages.Regulatory, scope);
            Assert.Contains(Stages.Dosing, scope);
            Assert.Contains(Stages.Decision, scope);
        }
    }

    [Fact]
    public void EveryScope_NamesOnlyRealStages()
    {
        // A typo here is not a compile error -- it is a stage reset that silently never happens.
        foreach (var (field, scope) in RerunScope.Map)
        foreach (var stage in scope)
            Assert.True(Stages.All.Contains(stage) || stage == Stages.Background,
                $"'{field}' declares an unknown stage '{stage}'");
    }

    [Fact]
    public void SignaturesAtRisk_NamesOnlyTheGatesTheScopeActuallyReaches()
    {
        // A batch-mass amendment must not warn about the R.E.'s signature: it does not reach Regulatory, and
        // a confirmation prompt that cries wolf is one the operator learns to click through.
        Assert.Equal([GateTypes.Vp],
            RerunScope.SignaturesAtRisk(RerunScope.For("batchMassKg"), regulatorySigned: true, vpSigned: true));

        Assert.Equal([GateTypes.Regulatory, GateTypes.Vp],
            RerunScope.SignaturesAtRisk(RerunScope.For("markets"), regulatorySigned: true, vpSigned: true));
    }

    [Fact]
    public void SignaturesAtRisk_IsEmpty_WhenNothingIsSigned()
    {
        // Nothing signed ⇒ nothing to warn about ⇒ the amendment just runs. "Nothing waits" is the default;
        // the confirmation is the exception, and it has to stay rare to keep meaning anything.
        Assert.Empty(RerunScope.SignaturesAtRisk(
            RerunScope.For("material"), regulatorySigned: false, vpSigned: false));
    }
}
