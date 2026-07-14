using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// <param name="MeasuredBackground">The physicist's measured background, and <paramref name="Device"/> the
/// deployment XRF unit whose LODs the ppm floor targets. BOTH OPTIONAL, deliberately: the XRF run happens
/// offline and can land days after the project is opened (UX spec Law 6). Dosing PARKS on their absence —
/// that is the awaiting-physics state — so intake must not demand them up front.</param>
public sealed record CreateProjectRequest(
    string Client, string Product,
    List<ComponentSpec> Components,
    List<ElementPool> ElementPools,
    List<CandidateSubstance>? Candidates,
    List<string>? ClientRestrictedList,
    List<MeasuredBackground>? MeasuredBackground = null,
    XrfDevice? Device = null)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Client) || string.IsNullOrWhiteSpace(Product)) return "client and product are required";
        if (Components is not { Count: > 0 }) return "at least one component is required";
        if (Components.Select(c => c.Id).Distinct().Count() != Components.Count) return "component ids must be unique";
        var hasPools = ElementPools is { Count: > 0 };
        var hasCandidates = Candidates is { Count: > 0 };
        if (!hasPools && !hasCandidates) return "provide element pools (production) or explicit candidates (known-candidate mode)";
        var componentIds = Components.Select(c => c.Id).ToHashSet();
        if (hasPools && ElementPools.Any(p => !componentIds.Contains(p.Component)))
            return "every element pool must reference a declared component";
        // Anti-rubber-stamping (design §4): a conditional (L) pool entry must carry its signal-character note.
        if (hasPools && ElementPools.Any(p => p.Status == "L" && string.IsNullOrWhiteSpace(p.SignalNote)))
            return "each conditional (L) element pool entry must carry a signal-character note";
        if (hasCandidates && Candidates!.Any(c => !componentIds.Contains(c.ComponentId)))
            return "every candidate must reference a declared component";
        // A background measured on a component this product does not have is a measurement of nothing. It
        // would sit in the payload looking exactly like data, while the component it was really measured on
        // silently has no background at all — and the ppm floor would be computed without it.
        if (MeasuredBackground is { Count: > 0 } && MeasuredBackground.Any(b => !componentIds.Contains(b.Component)))
            return "every measured background must reference a declared component";
        return null;
    }
}
