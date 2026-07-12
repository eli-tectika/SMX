namespace Smx.Domain.Records;

public sealed class CandidatesDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Candidates;
    public List<CandidateSubstance> Substances { get; set; } = [];
}
