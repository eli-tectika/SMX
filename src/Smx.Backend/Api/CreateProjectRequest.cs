using Smx.Domain.Records;

namespace Smx.Backend.Api;

public sealed record CreateProjectRequest(
    string Client, string Product,
    List<ComponentSpec> Components,
    List<ElementPool> ElementPools,
    List<CandidateSubstance>? Candidates,
    List<string>? ClientRestrictedList)
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
        return null;
    }
}
