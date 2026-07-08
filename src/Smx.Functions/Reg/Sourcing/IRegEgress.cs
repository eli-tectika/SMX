using Smx.Functions.Sds.Domain; // reuse EgressResult — the HTTP fetch contract is identical

namespace Smx.Functions.Reg.Sourcing;

// The single controlled outbound path for regulatory fetches (NAT egress + regulator allowlist). Mirrors
// Sds.Sourcing.IEgressClient but is a distinct type so DI never confuses the two allowlists. Returns null on
// any non-success / disallowed host.
public interface IRegEgress
{
    Task<EgressResult?> FetchAsync(Uri url, CancellationToken ct);
}
