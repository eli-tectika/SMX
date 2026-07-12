namespace Smx.Backend.Api;

public sealed record ReviewRequest(string Cas, string ComponentId);
public sealed record DeterminationRequest(string Cas, string ComponentId, string Determination, string? Reason);
