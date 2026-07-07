using System.Text;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

public sealed class DryRunEgressClient : IEgressClient
{
    private readonly Func<Uri, EgressResult?> _responder;
    public DryRunEgressClient(Func<Uri, EgressResult?> responder) => _responder = responder;

    // Default: return a canned SDS-shaped payload for any allowlisted PDF url, an html stub otherwise.
    public static DryRunEgressClient Default(byte[] cannedPdf) => new(url =>
        url.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? new EgressResult(cannedPdf, "application/pdf", url)
            : new EgressResult(Encoding.UTF8.GetBytes("<html>dry-run</html>"), "text/html", url));

    public Task<EgressResult?> FetchAsync(Uri url, CancellationToken ct) => Task.FromResult(_responder(url));
}
