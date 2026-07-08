using Smx.Functions.Reference.Domain;

namespace Smx.ReferenceData.Transform;

public static class Mappers
{
    public static IReadOnlyList<CompatibilityDoc> CompatibilityRules(IEnumerable<SheetRow> rows)
    {
        var list = new List<CompatibilityDoc>();
        foreach (var r in rows)
        {
            var element = r.Get("Element / Pair / Form / Class");
            var dimension = r.Get("Dimension");
            var substrate = r.Get("Substrate / Application");
            if (element.Length == 0 || dimension.Length == 0) continue;
            var disc = $"{dimension}-{substrate}";
            list.Add(new CompatibilityDoc(
                Id: ReferenceKey.DocId(ReferenceDocType.Rule, element, disc),
                Element: element, DocType: ReferenceDocType.Rule,
                Dimension: dimension, Substrate: substrate, Subject: element,
                Verdict: r.Get("Verdict"), Reason: r.Get("Reason"),
                RefIds: SheetReader.RefIds(r.Get("Key Ref(s)"))));
        }
        return list;
    }

    public static IReadOnlyList<CompatibilityDoc> GoldSolubility(IEnumerable<SheetRow> rows)
    {
        var list = new List<CompatibilityDoc>();
        foreach (var r in rows)
        {
            var element = r.Get("Element").TrimEnd('*');
            if (element.Length == 0) continue;
            list.Add(new CompatibilityDoc(
                Id: ReferenceKey.DocId(ReferenceDocType.GoldSolubility, element, "gold"),
                Element: element, DocType: ReferenceDocType.GoldSolubility, Substrate: "Gold",
                SystemType: r.Get("System type"),
                MaxSolubilityAtPct: r.Get("Max solid solubility (at%)"),
                MaxSolubilityWtPct: r.Get("Max solid solubility (wt%)"),
                TempOfMaxC: r.Get("T of max (C)"),
                RetainedNearRt: r.Get("Retained near RT?"),
                Source: r.Get("Source"), Verification: r.Get("Verification")));
        }
        return list;
    }

    public static IReadOnlyList<CompatibilityDoc> IcpInterference(IEnumerable<SheetRow> rows)
    {
        var list = new List<CompatibilityDoc>();
        foreach (var r in rows)
        {
            var analyte = r.Get("Analyte (mass / line)");
            if (analyte.Length == 0) continue;
            var element = analyte.Split(' ', '(')[0].Trim();
            list.Add(new CompatibilityDoc(
                Id: ReferenceKey.DocId(ReferenceDocType.IcpInterference, element, $"{r.Get("Technique")}-{r.Get("Interferent")}"),
                Element: element, DocType: ReferenceDocType.IcpInterference,
                Technique: r.Get("Technique"), Subject: analyte,
                Interferent: r.Get("Interferent"), InterferingSpecies: r.Get("Interfering species"),
                Severity: r.Get("Severity"), Mitigation: r.Get("Mitigation"),
                RefIds: SheetReader.RefIds(r.Get("Ref"))));
        }
        return list;
    }

    public static IReadOnlyList<BibliographyDoc> Bibliography(IEnumerable<SheetRow> rows)
    {
        var list = new List<BibliographyDoc>();
        foreach (var r in rows)
        {
            var refId = r.Get("Ref ID");
            if (refId.Length == 0) continue;
            list.Add(new BibliographyDoc(
                Id: refId, RefId: refId, Title: r.Get("Title / Citation"),
                Source: r.Get("Source"), Year: r.Get("Year"), Type: r.Get("Type"),
                Doi: r.Get("DOI / URL / Identifier"), Dimension: r.Get("Dimension"),
                Substrate: r.Get("Substrate"), Elements: SheetReader.RefIds(r.Get("Elements")),
                WhatItEstablishes: r.Get("What it establishes"), Verification: r.Get("Verification")));
        }
        return list;
    }

    public static IReadOnlyList<SupplierDoc> Suppliers(IEnumerable<SheetRow> rows)
    {
        var list = new List<SupplierDoc>();
        foreach (var r in rows)
        {
            var supplier = r.Get("Supplier");
            if (supplier.Length == 0) continue;
            list.Add(new SupplierDoc(
                Id: ReferenceKey.Slug(supplier), Supplier: supplier,
                Status: r.Get("Status"), Category: r.Get("Category"), HqCountry: r.Get("HQ Country"),
                Address: r.Get("Full Address"), Website: r.Get("Website"),
                Contact: r.Get("Contact (email / phone)"), ProductCategories: r.Get("Product Categories"),
                ElementsCovered: r.Get("Marker Elements Covered"), Forms: r.Get("Forms / Molecules"),
                Pricing: r.Get("Pricing"), Lists: new[] { "master" }));
        }
        return list;
    }

    public static IReadOnlyList<CatalogDoc> CatalogProducts(IEnumerable<SheetRow> rows)
    {
        var list = new List<CatalogDoc>();
        foreach (var r in rows)
        {
            var element = r.Get("Element(s)");
            var supplier = r.Get("Supplier");
            if (element.Length == 0) continue;
            list.Add(new CatalogDoc(
                Id: ReferenceKey.DocId(ReferenceDocType.Product, element, $"{r.Get("Molecule (full name)")}-{supplier}"),
                Element: element, DocType: ReferenceDocType.Product,
                Compound: r.Get("Compound / Form"), Molecule: r.Get("Molecule (full name)"),
                Cas: r.Get("CAS"), Purity: r.Get("Purity"), Supplier: supplier,
                Price: r.Get("Price"), Pack: r.Get("Pack / Quantity"), Notes: r.Get("Notes / Source")));
        }
        return list;
    }

    public static IReadOnlyList<CatalogDoc> CatalogElements(IEnumerable<SheetRow> rows)
    {
        var list = new List<CatalogDoc>();
        foreach (var r in rows)
        {
            var symbol = r.Get("Symbol");
            var element = symbol.Length > 0 ? symbol : r.Get("Element");
            if (element.Length == 0) continue;
            list.Add(new CatalogDoc(
                Id: ReferenceKey.DocId(ReferenceDocType.ElementForms, element, "forms"),
                Element: element, DocType: ReferenceDocType.ElementForms,
                Symbol: symbol, Group: r.Get("Group"), Forms: r.Get("SMX-relevant forms"),
                ApplicationNotes: r.Get("Application notes (oil / gold / solids)"),
                ExampleMolecule: r.Get("Example molecule (CAS)"),
                ExampleSuppliers: r.Get("Example suppliers")));
        }
        return list;
    }

    /// <summary>Search chunks from the rule reasons + bibliography, each carrying citation metadata.</summary>
    public static IReadOnlyList<ReferenceChunkSeed> SearchChunks(
        IEnumerable<CompatibilityDoc> rules, IEnumerable<BibliographyDoc> biblio, string dataset)
    {
        var list = new List<ReferenceChunkSeed>();
        foreach (var d in rules)
        {
            var refIds = d.RefIds ?? Array.Empty<string>();
            // Invariant 4: only emit a chunk that carries a citation handle (>=1 refId).
            if (string.IsNullOrWhiteSpace(d.Reason) || refIds.Count == 0) continue;
            list.Add(new ReferenceChunkSeed(
                Id: $"chunk|{d.Id}", Content: d.Reason!, Element: d.Element, Substrate: d.Substrate,
                Dimension: d.Dimension, Verdict: d.Verdict, RefIds: refIds,
                SourceTitle: $"Compatibility Rules — {d.Element} ({d.Dimension})",
                Doi: null, Url: null, Sheet: "Compatibility Rules", Dataset: dataset));
        }
        foreach (var b in biblio)
        {
            if (string.IsNullOrWhiteSpace(b.WhatItEstablishes)) continue;
            list.Add(new ReferenceChunkSeed(
                Id: $"chunk|bib|{b.RefId}", Content: b.WhatItEstablishes!, Element: string.Join(",", b.Elements),
                Substrate: b.Substrate, Dimension: b.Dimension, Verdict: null, RefIds: new[] { b.RefId },
                SourceTitle: b.Title, Doi: b.Doi, Url: null, Sheet: "Reference Library", Dataset: dataset));
        }
        return list;
    }
}
