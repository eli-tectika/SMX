using System.Text.RegularExpressions;

namespace Smx.Functions.Reference.Domain;

/// <summary>Every Cosmos-bound reference doc exposes its id so stores/fakes can key on it.</summary>
public interface IHasId { string Id { get; } }

public static class ReferenceDocType
{
    public const string Rule = "rule";
    public const string GoldSolubility = "goldSolubility";
    public const string XrfLines = "xrfLines";
    public const string IcpInterference = "icpInterference";
    public const string Product = "product";
    public const string ElementForms = "elementForms";
}

public static class ReferenceKey
{
    public static string Slug(string s)
        => Regex.Replace(Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " "), @"[^a-z0-9]+", "-").Trim('-');

    public static string DocId(string docType, string element, string discriminator)
        => $"{docType}|{element.Trim()}|{Slug(discriminator)}";
}

// ---- ref-compatibility container (partition: /element) ----
public sealed record LineOverlap(
    string LineA, double? EnergyA, string ConflictsWith, string LineB, double? EnergyB,
    string Severity, string? Note, IReadOnlyList<string> RefIds);

/// <summary>Union doc for the ref-compatibility container; DocType discriminates the payload.</summary>
public sealed record CompatibilityDoc(
    string Id, string Element, string DocType,
    string? Dimension = null, string? Substrate = null, string? Subject = null,
    string? Verdict = null, string? Reason = null,
    string? SystemType = null, string? MaxSolubilityAtPct = null, string? MaxSolubilityWtPct = null,
    string? TempOfMaxC = null, string? RetainedNearRt = null, string? Source = null, string? Verification = null,
    int? Z = null, double? Ka1KeV = null, double? La1KeV = null, IReadOnlyList<LineOverlap>? Overlaps = null,
    string? Technique = null, string? Interferent = null, string? InterferingSpecies = null,
    string? Severity = null, string? Mitigation = null,
    IReadOnlyList<string>? RefIds = null) : IHasId;

// ---- ref-bibliography container (partition: /refId) ----
public sealed record BibliographyDoc(
    string Id, string RefId, string Title, string? Source, string? Year, string? Type,
    string? Doi, string? Dimension, string? Substrate, IReadOnlyList<string> Elements,
    string? WhatItEstablishes, string? Verification) : IHasId;

// ---- ref-suppliers container (partition: /supplier) ----
public sealed record SupplierDoc(
    string Id, string Supplier, string? Status, string? Category, string? HqCountry,
    string? Address, string? Website, string? Contact, string? ProductCategories,
    string? ElementsCovered, string? Forms, string? Pricing, IReadOnlyList<string> Lists) : IHasId;

// ---- ref-catalog container (partition: /element) ----
public sealed record CatalogDoc(
    string Id, string Element, string DocType,
    string? Compound = null, string? Molecule = null, string? Cas = null, string? Purity = null,
    string? Supplier = null, string? Price = null, string? Pack = null, string? Notes = null,
    string? Symbol = null, string? Group = null, string? Forms = null, string? ApplicationNotes = null,
    string? ExampleMolecule = null, string? ExampleSuppliers = null) : IHasId;

// ---- smx-reference index ----
/// <summary>Chunk as committed in search-chunks.json — no vector yet.</summary>
public sealed record ReferenceChunkSeed(
    string Id, string Content, string? Element, string? Substrate, string? Dimension,
    string? Verdict, IReadOnlyList<string> RefIds, string? SourceTitle, string? Doi,
    string? Url, string Sheet, string Dataset);

/// <summary>Chunk pushed to the index — vector filled by the seeder.</summary>
public sealed record ReferenceChunk(
    string Id, string Content, float[] ContentVector, string? Element, string? Substrate,
    string? Dimension, string? Verdict, IReadOnlyList<string> RefIds, string? SourceTitle,
    string? Doi, string? Url, string Sheet, string Dataset);
