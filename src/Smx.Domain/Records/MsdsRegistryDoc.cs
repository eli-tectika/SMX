namespace Smx.Domain.Records;

/// Thin curated governance layer over the SDS corpus (design §6.3). PK /cas. References the
/// indexed SDS; does not duplicate the corpus.
///
/// It no longer backs MSDS-before-order. That gate survives — procurement still cannot run without a
/// safety sheet — but its predicate moved from "a human signed this sheet" to "a validated, indexed
/// sheet exists for this CAS" (design 2026-07-29, D8/D9), and the sheet's existence is a fact of the
/// corpus, not of this overlay. `ReviewStatus`/`ReviewedAt` are therefore gone; a signature nothing
/// reads is worse than no signature, because it still asks the operator to keep producing one.
///
/// What remains is `LinkedProjects` — which projects put this substance in play. That is the one
/// thing about a sheet the corpus genuinely cannot know.
public sealed class MsdsRegistryDoc
{
    public required string Id { get; set; }
    public string Type { get; set; } = KnowledgeTypes.MsdsRegistry;
    public required string Cas { get; set; }               // the partition key
    public required string Supplier { get; set; }
    public required string Version { get; set; }
    public required string Date { get; set; }              // SDS revision date (ISO-8601)
    public List<string> LinkedProjects { get; set; } = [];
}
