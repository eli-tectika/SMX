using Smx.Domain.Documents;
using Smx.Functions.Sds.Domain;   // DedupKey, compiled into this assembly — see the csproj

namespace Smx.Backend.Tests;

/// SdsRegistryKey exists to mirror DedupKey.Norm/ForRegistry on the read side, and a mirror that
/// drifts is worse than no mirror: the SDS half of the text view would go silently empty again,
/// which is a false provenance claim rather than a visible failure.
///
/// DedupKey.cs is compiled into this test assembly rather than quoted, so these assertions run
/// against the real function and an edit in Smx.Functions breaks a test here.
public class SdsRegistryKeyTests
{
    /// The bug this whole class exists to kill: IngestionPipeline pushes the RAW supplier into
    /// sds-index while the registry id holds the normalised one, and every supplier in
    /// Sds/Config/suppliers.allowlist.json is mixed-case. "Sigma-Aldrich" must reconstruct to the
    /// "sigma-aldrich" the id carries, or an `eq` filter built from the id matches nothing, forever.
    [Theory]
    [InlineData("7761-88-8", "Sigma-Aldrich", "2024-03-11")]
    [InlineData("1313-97-9", "Alfa Aesar (Fisher)", "2023-11-02")]
    [InlineData("7440-16-6", "Thermo Fisher Scientific", "2025-01-01")]
    [InlineData("1314-36-9", "TCI AMERICA", "2022-06-30")]
    // Whitespace runs collapse rather than strip, which is why a space is legal in an `sds` payload.
    [InlineData("  7761-88-8 ", "  Alfa   Aesar  ", " 2024-03-11 ")]
    public void AgreesWithDedupKeyForRegistry(string cas, string supplier, string revisionDate)
        => Assert.Equal(
            DedupKey.ForRegistry(cas, supplier, revisionDate),
            SdsRegistryKey.ForRegistry(cas, supplier, revisionDate));

    [Fact]
    public void AMixedCaseSupplierNormalisesToTheIdItIsStoredUnder()
    {
        // Spelled out rather than only compared to DedupKey, so the expected shape is visible here:
        // this literal is what an `sds` document id's payload contains.
        Assert.Equal("7761-88-8|sigma-aldrich|2024-03-11",
                     SdsRegistryKey.ForRegistry("7761-88-8", "Sigma-Aldrich", "2024-03-11"));
        Assert.Equal("1313-97-9|alfa aesar (fisher)|2023-11-02",
                     SdsRegistryKey.ForRegistry("1313-97-9", "Alfa Aesar (Fisher)", "2023-11-02"));
    }

    [Theory]
    [InlineData("  Sigma  ", "sigma")]
    [InlineData("Alfa\tAesar", "alfa aesar")]
    [InlineData("Alfa \n Aesar", "alfa aesar")]
    [InlineData("THERMO", "thermo")]
    [InlineData("", "")]
    public void NormTrimsLowercasesAndCollapsesWhitespaceRuns(string input, string expected)
        => Assert.Equal(expected, SdsRegistryKey.Norm(input));

    // A projected search row binds every field nullable, and a null one must reconstruct to a key
    // that simply fails to match — never to a NullReferenceException on the text endpoint.
    [Fact]
    public void ANullFieldNormalisesToEmptyRatherThanThrowing()
    {
        Assert.Equal("", SdsRegistryKey.Norm(null));
        Assert.Equal("7761-88-8||2024-03-11", SdsRegistryKey.ForRegistry("7761-88-8", null, "2024-03-11"));
    }
}
