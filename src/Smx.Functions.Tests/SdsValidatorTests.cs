using Smx.Functions.Sds.Ingestion;
using Xunit;

public class SdsValidatorTests
{
    private static readonly IReadOnlySet<string> Allow = new HashSet<string> { "sigmaaldrich.com", "chemblink.com" };
    private static string Sample() => File.ReadAllText("Resources/sample_sds.txt");
    private readonly SdsValidator _v = new(minGhsSections: 10);

    [Fact]
    public void Accepts_real_sds_with_matching_cas_from_allowlisted_domain()
        => Assert.True(_v.Validate(Sample(), "1310-73-2", "sigmaaldrich.com", Allow).Ok);

    [Fact]
    public void Rejects_when_cas_absent()
    {
        var r = _v.Validate(Sample(), "7440-02-0", "sigmaaldrich.com", Allow);
        Assert.False(r.Ok);
        Assert.Contains("CAS", r.Reason);
    }

    [Fact]
    public void Rejects_non_sds_document()
    {
        var r = _v.Validate("This is an invoice. CAS-No: 1310-73-2", "1310-73-2", "sigmaaldrich.com", Allow);
        Assert.False(r.Ok);
        Assert.Contains("GHS", r.Reason);
    }

    [Fact]
    public void Rejects_off_allowlist_domain()
    {
        var r = _v.Validate(Sample(), "1310-73-2", "evil.example", Allow);
        Assert.False(r.Ok);
        Assert.Contains("domain", r.Reason);
    }
}
