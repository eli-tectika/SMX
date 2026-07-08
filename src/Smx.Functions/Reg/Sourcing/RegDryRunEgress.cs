using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Reg.Sourcing;

// Dry-run egress for local runs / tests — no outbound calls. Mirrors Sds/Sourcing/DryRunEgressClient.
public sealed class RegDryRunEgress : IRegEgress
{
    private readonly Func<Uri, EgressResult?> _responder;
    public RegDryRunEgress(Func<Uri, EgressResult?> responder) => _responder = responder;

    public static RegDryRunEgress Default(byte[] canned, string contentType = "text/csv")
        => new(url => new EgressResult(canned, contentType, url));

    public Task<EgressResult?> FetchAsync(Uri url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        => Task.FromResult(_responder(url));
}
