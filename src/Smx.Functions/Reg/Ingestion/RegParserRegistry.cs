namespace Smx.Functions.Reg.Ingestion;

// Resolves the IRegParser named by a RegSource.Parser. Registered parsers are injected by DI; an unknown name
// is a configuration error surfaced loudly (not a silent skip that would drop a source from the corpus).
public sealed class RegParserRegistry
{
    private readonly IReadOnlyDictionary<string, IRegParser> _byName;

    public RegParserRegistry(IEnumerable<IRegParser> parsers)
        => _byName = parsers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public IRegParser Get(string name)
        => _byName.TryGetValue(name, out var p) ? p
           : throw new InvalidOperationException($"No IRegParser registered for '{name}'.");
}
