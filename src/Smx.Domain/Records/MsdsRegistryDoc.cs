namespace Smx.Domain.Records;

/// Thin curated governance layer over the SDS corpus (design §6.3). PK /cas. References the
/// indexed SDS; does not duplicate the corpus. Backs the MSDS-before-order precondition (Plan 5).
public sealed class MsdsRegistryDoc
{
    public required string Id { get; set; }
    public string Type { get; set; } = KnowledgeTypes.MsdsRegistry;
    public required string Cas { get; set; }               // the partition key
    public required string Supplier { get; set; }
    public required string Version { get; set; }
    public required string Date { get; set; }              // SDS revision date (ISO-8601)
    public string ReviewStatus { get; set; } = "unreviewed"; // "unreviewed" | "reviewed"
    public List<string> LinkedProjects { get; set; } = [];
}
