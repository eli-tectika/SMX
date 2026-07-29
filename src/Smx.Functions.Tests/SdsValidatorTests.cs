using Smx.Functions.Sds.Ingestion;
using Xunit;

public class SdsValidatorTests
{
    private static string Sample() => File.ReadAllText("Resources/sample_sds.txt");
    private readonly SdsValidator _v = new(minGhsSections: 10);

    [Fact]
    public void Accepts_real_sds_with_matching_cas()
        => Assert.True(_v.Validate(Sample(), "1310-73-2").Ok);

    // The load-bearing inversion: the domain check is gone, so a sheet is judged only by its CONTENT.
    // This is what lets web discovery contribute at all, and it costs nothing — the domain never told
    // us whether a document was the RIGHT document. (Was `Rejects_off_allowlist_domain`.)
    [Fact]
    public void A_valid_sheet_needs_no_curated_provenance()
        => Assert.True(_v.Validate(Sample(), "1310-73-2").Ok);

    [Fact]
    public void Rejects_when_cas_absent()
    {
        var r = _v.Validate(Sample(), "7440-02-0");
        Assert.False(r.Ok);
        Assert.Contains("CAS", r.Reason);
    }

    // The other half of the bargain: relaxing provenance is only safe because content is checked
    // strictly. A sheet for the wrong substance is refused however reputable the host that served it.
    [Fact]
    public void Rejects_the_wrong_substance()
    {
        var r = _v.Validate(Sample(), "7440-25-7");
        Assert.False(r.Ok);
        Assert.Contains("7440-25-7", r.Reason);
    }

    [Fact]
    public void Rejects_non_sds_document()
    {
        var r = _v.Validate("This is an invoice. CAS-No: 1310-73-2", "1310-73-2");
        Assert.False(r.Ok);
        Assert.Contains("GHS", r.Reason);
    }
}
