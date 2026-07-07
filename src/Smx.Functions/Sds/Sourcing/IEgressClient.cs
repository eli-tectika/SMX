using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

// THE single outbound path. Injected only into SdsSweep. Returns null on any non-success / disallowed host.
public interface IEgressClient
{
    Task<EgressResult?> FetchAsync(Uri url, CancellationToken ct);
}
