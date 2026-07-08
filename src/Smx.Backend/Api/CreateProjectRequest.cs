using Smx.Domain.Records;

namespace Smx.Backend.Api;

public sealed record CreateProjectRequest(
    string Client, string Product,
    List<ComponentSpec> Components, List<SubstanceSpec> Substances,
    List<string>? ClientRestrictedList)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Client) || string.IsNullOrWhiteSpace(Product)) return "client and product are required";
        if (Components is not { Count: > 0 }) return "at least one component is required";
        if (Substances is not { Count: > 0 }) return "at least one candidate substance is required";
        if (Components.Select(c => c.Id).Distinct().Count() != Components.Count) return "component ids must be unique";
        if (Substances.Select(s => s.Cas).Distinct().Count() != Substances.Count) return "substance CAS numbers must be unique";
        return null;
    }
}
