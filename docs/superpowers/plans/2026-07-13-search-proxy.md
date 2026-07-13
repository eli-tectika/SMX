# Search Proxy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the anonymizing Search Proxy Function and give the Discovery agent a `search_web` tool that can reach beyond the seeded catalog without leaking which candidate a live project is considering.

**Architecture:** A new, standalone .NET 8 isolated Functions app (`Smx.SearchProxy`) deployed to the already-provisioned, empty `func-*-searchproxy-*` app. It exposes one endpoint, `POST /api/search`, whose contract is *project-blind* (there is no field a project id could travel in). Each real query is issued to Brave inside a shuffled batch of decoys drawn from a git-versioned corpus of the seeded catalog's chemistry (k-anonymity), and there is no fetch interface, so third-party hosts never see us. On the agent side, `WebSearchTool` (in `Smx.Infrastructure`) rejects queries containing the client/product/project names before they leave the VNet, and deterministic rails in `DiscoveryAgent.Validate` stop a web-only citation from ever producing a Tier A or `preferred` candidate.

**Tech Stack:** .NET 8 isolated Azure Functions, xUnit (hand-written fakes — no Moq/WireMock), Azure Blob (cache + quota, on the proxy's own storage), Brave Search API, Entra Easy Auth, Bicep.

**Spec:** [`docs/superpowers/specs/2026-07-13-search-proxy-design.md`](../specs/2026-07-13-search-proxy-design.md). Read §3 (hard invariants) before starting — every one of them has a test in this plan.

---

## Conventions you must follow

This codebase has strong, deliberate conventions. Match them; do not invent new ones.

- **Options:** a `sealed class` with `init` properties, inline defaults, and a `static From(IConfiguration c)` reading flat `SCREAMING_SNAKE` keys with `??` / `TryParse` defaults. **No `IOptions<T>`, no `Configuration.Bind()`.** Register as a singleton and inject the concrete type. Model: `src/Smx.Functions/Sds/Config/SdsOptions.cs`.
- **Egress:** failure returns `null`, it never throws — except `OperationCanceledException`, which is rethrown *before* the general catch. Retry 5xx (and 429) only; 4xx is permanent. Linear backoff `attempt * 2` seconds. Model: `src/Smx.Functions/Reg/Sourcing/RegNatEgressClient.cs`.
- **Dry-run twin:** every egress component ships a `*_DRY_RUN` fake selected in `Program.cs`, so the whole app runs with no key and no network. Model: `src/Smx.Functions/Program.cs:84-93`.
- **Triggers are thin shells** over a testable core that takes `string nowUtc` as a parameter (never `DateTime.UtcNow` inside) so tests are deterministic. Model: `SdsSweep.RunSweepAsync(nowUtc, ct)`.
- **HTTP triggers are `AuthorizationLevel.Anonymous`** with a comment that Easy Auth is the infra-layer control.
- **Git-versioned JSON for security-critical data**, loaded by a provider that **throws if empty**. Model: `src/Smx.Functions/Sds/Sourcing/AllowlistProvider.cs`.
- **Tests:** xUnit + hand-written fakes in a `Fakes/` folder. `NullLogger<T>.Instance` for loggers. Stub `HttpMessageHandler` when you must exercise a real client pipeline (model: `src/Smx.Orchestrator.Tests/LearnedConclusionsSearchToolTests.cs:26-40`).

**Build/test commands:**
```bash
dotnet build src/Smx.Functions.sln && dotnet test src/Smx.Functions.sln
dotnet build src/Smx.Backend.sln  && dotnet test src/Smx.Backend.sln
```

---

## File structure

**New — the proxy (deployed to `func-*-searchproxy-*`, an app with ZERO corpus RBAC):**

| File | Responsibility |
|---|---|
| `src/Smx.SearchProxy.Contracts/SearchContracts.cs` | The wire contract. Zero dependencies, shared with `Smx.Infrastructure`. |
| `src/Smx.SearchProxy/Config/ProxyOptions.cs` | Config binding. |
| `src/Smx.SearchProxy/Config/cover-corpus.json` | Git-versioned decoy queries (generated, PR-reviewed). |
| `src/Smx.SearchProxy/Anonymity/StructuralGuard.cs` | Project-blind rejection of identifier-shaped queries. |
| `src/Smx.SearchProxy/Anonymity/CoverCorpus.cs` | Loads + validates the decoy corpus; throws if thin. |
| `src/Smx.SearchProxy/Anonymity/CoverBatch.cs` | Real query + N−1 decoys, shuffled. |
| `src/Smx.SearchProxy/Providers/ISearchProvider.cs` | `null` = failed. One method. |
| `src/Smx.SearchProxy/Providers/BraveSearchProvider.cs` | Single-host allowlist, retry, timeout, header hygiene. |
| `src/Smx.SearchProxy/Providers/DryRunSearchProvider.cs` | Canned results, zero egress. |
| `src/Smx.SearchProxy/Pipeline/SearchPipeline.cs` | The testable core: guard → quota → cache → cover → provider → cache-all → audit. |
| `src/Smx.SearchProxy/Pipeline/ISearchCache.cs`, `BlobSearchCache.cs`, `CacheKey.cs` | Content-addressed result cache with TTL. |
| `src/Smx.SearchProxy/Pipeline/QuotaGuard.cs`, `IQuotaStore.cs`, `BlobQuotaStore.cs` | Monthly cap + per-minute bucket. |
| `src/Smx.SearchProxy/Pipeline/EgressAudit.cs` | One structured App Insights event per request. |
| `src/Smx.SearchProxy/Triggers/SearchHttp.cs`, `HealthHttp.cs` | Thin shells. |
| `src/Smx.SearchProxy/Program.cs` | DI + the dry-run switch. |
| `src/Smx.SearchProxy.Tests/**` | Tests for all of the above. |
| `tools/Smx.CoverCorpus/Program.cs` | Offline generator: catalog seed → `cover-corpus.json`. |

**New — the agent side:**

| File | Responsibility |
|---|---|
| `src/Smx.Domain/CasNumber.cs` | CAS check-digit validation. |
| `src/Smx.Infrastructure/Search/SearchProxyClient.cs` | Entra-token HTTP client to the proxy. |
| `src/Smx.Infrastructure/Search/SensitiveTermGuard.cs` | Per-project term rejection (the only layer that knows the names). |
| `src/Smx.Infrastructure/Search/WebSearchTool.cs` | `IWebSearch` impl: kill switch → guard → budget → client. |

**Modified:**

| File | Change |
|---|---|
| `src/Smx.Domain/Tools/ITools.cs` | `+ IWebSearch`, `+ WebHit`. |
| `src/Smx.Orchestrator/Agents/ToolBox.cs` | `DiscoveryTools(SensitiveTerms)` gains `search_web`. |
| `src/Smx.Orchestrator/Agents/DiscoveryAgent.cs` | Instructions + the two `Validate` rails. |
| `src/Smx.Orchestrator/Dispatch/AgentRuns.cs` | `RunDiscoveryAsync` gains a `ProjectDoc`. |
| `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs` | Loads and passes the `ProjectDoc`. |
| `src/Smx.Orchestrator/Program.cs` | DI for the web-search stack. |
| `src/Smx.Infrastructure/BackendOptions.cs` | `+ SearchProxyEndpoint`, `SearchProxyAudience`, `WebSearchEnabled`, `WebSearchMaxPerStage`. |
| `src/Smx.Functions/Reference/Seed/catalog-{products,elements}.json` | Fix the invalid CAS `15492-49-8` → `15492-49-6`. |
| `infra/modules/functions.bicep` + `infra/single-rg/modules/functions.bicep` | Proxy app settings, Easy Auth, cache container, KV grant. |
| `infra/modules/compute.bicep` + single-rg twin | Orchestrator env vars. |
| `infra/scripts/publish-searchproxy.{sh,ps1}`, `set-search-key.{sh,ps1}`, `configure-auth.{sh,ps1}` | Twin pairs. |

---

## Task 0: Fix the invalid CAS in the seeded catalog

The catalog contains `15492-49-8` for Scandium tris(TMHD). Its check digit is wrong — the correct CAS for the anhydrous compound is **`15492-49-6`** (ChemicalBook CB1190609; Santa Cruz Biotechnology). Task 11 adds a validator that will reject the bad value, so fix the data first, in its own commit, so the fix is reviewable on its own.

**Files:**
- Modify: `src/Smx.Functions/Reference/Seed/catalog-products.json`
- Modify: `src/Smx.Functions/Reference/Seed/catalog-elements.json`

- [ ] **Step 1: Confirm the two occurrences**

```bash
grep -rn "15492-49-8" src/Smx.Functions/Reference/Seed/
```
Expected: one hit in each file.

- [ ] **Step 2: Fix both**

```bash
sed -i 's/15492-49-8/15492-49-6/g' src/Smx.Functions/Reference/Seed/catalog-products.json src/Smx.Functions/Reference/Seed/catalog-elements.json
grep -rn "15492-49" src/Smx.Functions/Reference/Seed/
```
Expected: two hits, both now `15492-49-6`.

- [ ] **Step 3: Commit**

```bash
git add src/Smx.Functions/Reference/Seed/
git commit -m "fix(reference): correct the Sc(TMHD)3 CAS — 15492-49-8 fails its check digit

The check digit of 15492-49-8 computes to 6, not 8. The anhydrous compound
is 15492-49-6. A wrong CAS in the catalog propagates silently into the
regulatory screen, the dosing maths and procurement — which is why Task 11
makes the check digit a hard validation rail."
```

> **Note for the implementer:** the seed workbooks in `data/` are the upstream source. This fix is applied to the generated seed JSON only; if `tools/Smx.ReferenceData.Transform` is ever re-run it will reintroduce the typo. Flag this to the operator — the workbook needs the same correction. Do **not** open the workbook to fix it yourself (ClosedXML rewrites it on disk).

---

## Task 1: The wire contract

A separate, zero-dependency project so the internet-facing app carries the contract types and *nothing else* of the domain.

**Files:**
- Create: `src/Smx.SearchProxy.Contracts/Smx.SearchProxy.Contracts.csproj`
- Create: `src/Smx.SearchProxy.Contracts/SearchContracts.cs`

- [ ] **Step 1: Create the project**

`src/Smx.SearchProxy.Contracts/Smx.SearchProxy.Contracts.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Write the contract**

`src/Smx.SearchProxy.Contracts/SearchContracts.cs`:
```csharp
namespace Smx.SearchProxy.Contracts;

/// The request the orchestrator sends to the Search Proxy.
///
/// PROJECT-BLIND BY CONSTRUCTION. There is deliberately no projectId, client, product, correlation-id or
/// url field here, and the trigger deserializes with UnmappedMemberHandling.Disallow — so a caller cannot
/// smuggle one in. This is stronger than scrubbing a project identifier out: there is nothing to scrub.
public sealed record SearchRequest(
    string Query,
    string Intent,
    int MaxResults = 10,
    int? FreshnessDays = null);

/// One normalized result. `Url` is where the operator can go to check the claim; the proxy itself never
/// fetches it (spec §3, invariant 2 — no fetch interface).
public sealed record SearchHit(
    string Title,
    string Url,
    string Snippet,
    string Host,
    string? Age);

/// `CoverCount` is how many queries actually egressed (real + decoys); 0 on a cache hit.
public sealed record SearchResponse(
    IReadOnlyList<SearchHit> Results,
    int ResultCount,
    bool CacheHit,
    int CoverCount);

/// `Reason` is a machine-readable token (e.g. "contains_guid"); the caller relays it to the model as an
/// instructive note so it can rephrase, rather than silently getting nothing back.
public sealed record SearchError(string Reason, string Message);

/// The intent selects which decoy family the cover batch is drawn from. Adding an intent means adding a
/// decoy family to cover-corpus.json — the corpus loader enforces that (CoverCorpus.FromJson throws if an
/// intent has no family), so a new intent cannot ship without its cover.
public static class SearchIntents
{
    public const string CandidateForms = "discovery.candidate_forms";
    public const string FormProperties = "discovery.form_properties";
    public const string SupplierAvailability = "discovery.supplier_availability";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { CandidateForms, FormProperties, SupplierAvailability };
}
```

- [ ] **Step 3: Build**

```bash
dotnet build src/Smx.SearchProxy.Contracts/Smx.SearchProxy.Contracts.csproj
```
Expected: `Build succeeded`.

- [ ] **Step 4: Commit**

```bash
git add src/Smx.SearchProxy.Contracts/
git commit -m "feat(proxy): the Search Proxy wire contract — project-blind by construction"
```

---

## Task 2: The proxy project skeleton + options

**Files:**
- Create: `src/Smx.SearchProxy/Smx.SearchProxy.csproj`, `host.json`, `Config/ProxyOptions.cs`
- Create: `src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj`, `ProxyOptionsTests.cs`
- Modify: `src/Smx.Functions.sln`

- [ ] **Step 1: Create the app project**

`src/Smx.SearchProxy/Smx.SearchProxy.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Smx.SearchProxy</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="1.22.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="1.17.4" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http" Version="3.2.0" />
    <PackageReference Include="Microsoft.ApplicationInsights.WorkerService" Version="2.22.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.ApplicationInsights" Version="1.4.0" />
    <PackageReference Include="Azure.Identity" Version="1.12.0" />
    <PackageReference Include="Azure.Storage.Blobs" Version="12.22.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Smx.SearchProxy.Contracts/Smx.SearchProxy.Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Update="host.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
    <None Update="Config/cover-corpus.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
  </ItemGroup>
</Project>
```

**Note the dependency list.** No Cosmos, no AI Search, no Data Lake, no OpenAI. That is not an oversight — this app's identity has zero corpus RBAC (`infra/modules/functions.bicep:92`) and it must stay unable to reach the corpus even if compromised. If you find yourself adding one of those packages, stop and re-read spec §2 D2.

`src/Smx.SearchProxy/host.json` (identical to the Functions app's):
```json
{
  "version": "2.0",
  "logging": {
    "applicationInsights": {
      "samplingSettings": { "isEnabled": true, "excludedTypes": "Request" }
    }
  }
}
```

- [ ] **Step 2: Write the failing options test**

`src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <RollForward>Major</RollForward>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="8.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Smx.SearchProxy/Smx.SearchProxy.csproj" />
  </ItemGroup>
</Project>
```

`src/Smx.SearchProxy.Tests/ProxyOptionsTests.cs`:
```csharp
using Microsoft.Extensions.Configuration;
using Smx.SearchProxy.Config;

namespace Smx.SearchProxy.Tests;

public class ProxyOptionsTests
{
    private static ProxyOptions From(params (string Key, string Value)[] pairs) =>
        ProxyOptions.From(new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build());

    [Fact]
    public void Defaults_AreTheSpecDefaults()
    {
        var o = From();
        Assert.Equal("brave", o.Provider);
        Assert.Equal(4, o.CoverCount);
        Assert.Equal(256, o.MaxQueryChars);
        Assert.Equal(10, o.MaxResults);
        Assert.Equal(168, o.CacheTtlHours);
        Assert.Equal(5000, o.MonthlyQueryCap);
        Assert.False(o.DryRun);
    }

    // Invariant 4: a config value must not be able to switch the anonymization off. An invariant with an
    // off switch is not an invariant — so PROXY_COVER_COUNT is clamped, not obeyed.
    [Theory]
    [InlineData("1", 2)]
    [InlineData("0", 2)]
    [InlineData("-5", 2)]
    [InlineData("6", 6)]
    public void CoverCount_IsClampedToAtLeastTwo(string configured, int expected) =>
        Assert.Equal(expected, From(("PROXY_COVER_COUNT", configured)).CoverCount);
}
```

- [ ] **Step 3: Run it — it must fail**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj
```
Expected: FAIL — `ProxyOptions` does not exist.

- [ ] **Step 4: Write `ProxyOptions`**

`src/Smx.SearchProxy/Config/ProxyOptions.cs`:
```csharp
using Microsoft.Extensions.Configuration;

namespace Smx.SearchProxy.Config;

public sealed class ProxyOptions
{
    public string Provider { get; init; } = "brave";
    public string ApiKey { get; init; } = "";
    public bool DryRun { get; init; }

    /// Real query + (CoverCount - 1) decoys. Clamped to >= 2 in From(): see CoverCountRaw.
    public int CoverCount { get; init; } = 4;
    /// What the operator actually configured, before clamping — Program.cs warns when they differ, so a
    /// misconfiguration is visible in the logs rather than silently corrected.
    public int CoverCountRaw { get; init; } = 4;
    public string CoverCorpusPath { get; init; } = "Config/cover-corpus.json";

    public int MaxQueryChars { get; init; } = 256;
    public int MaxResults { get; init; } = 10;
    public int TimeoutSeconds { get; init; } = 15;
    public int Retries { get; init; } = 3;
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;

    public int CacheTtlHours { get; init; } = 168;
    public string CacheContainer { get; init; } = "search-cache";
    public string StorageAccount { get; init; } = "";

    public int MonthlyQueryCap { get; init; } = 5000;
    public int RateLimitPerMinute { get; init; } = 30;

    public string? UamiClientId { get; init; }

    public static ProxyOptions From(IConfiguration c)
    {
        var coverRaw = int.TryParse(c["PROXY_COVER_COUNT"], out var cc) ? cc : 4;
        return new ProxyOptions
        {
            Provider = c["PROXY_PROVIDER"] ?? "brave",
            ApiKey = c["PROXY_SEARCH_API_KEY"] ?? "",
            DryRun = bool.TryParse(c["PROXY_DRY_RUN"], out var dr) && dr,
            CoverCountRaw = coverRaw,
            CoverCount = Math.Max(2, coverRaw),
            CoverCorpusPath = c["PROXY_COVER_CORPUS_PATH"] ?? "Config/cover-corpus.json",
            MaxQueryChars = int.TryParse(c["PROXY_MAX_QUERY_CHARS"], out var q) ? q : 256,
            MaxResults = int.TryParse(c["PROXY_MAX_RESULTS"], out var m) ? m : 10,
            TimeoutSeconds = int.TryParse(c["PROXY_TIMEOUT_SECONDS"], out var t) ? t : 15,
            Retries = int.TryParse(c["PROXY_RETRIES"], out var r) ? r : 3,
            CacheTtlHours = int.TryParse(c["PROXY_CACHE_TTL_HOURS"], out var ttl) ? ttl : 168,
            CacheContainer = c["PROXY_CACHE_CONTAINER"] ?? "search-cache",
            StorageAccount = c["AzureWebJobsStorage__accountName"] ?? "",
            MonthlyQueryCap = int.TryParse(c["PROXY_MONTHLY_QUERY_CAP"], out var cap) ? cap : 5000,
            RateLimitPerMinute = int.TryParse(c["PROXY_RATE_LIMIT_PER_MINUTE"], out var rl) ? rl : 30,
            UamiClientId = c["WORKLOAD_UAMI_CLIENT_ID"],
        };
    }
}
```

- [ ] **Step 5: Run the test — it must pass**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj
```
Expected: PASS, 2 tests (the `[Theory]` counts as 4 cases).

- [ ] **Step 6: Add both projects to the solution and build it**

```bash
dotnet sln src/Smx.Functions.sln add src/Smx.SearchProxy.Contracts/Smx.SearchProxy.Contracts.csproj \
                                     src/Smx.SearchProxy/Smx.SearchProxy.csproj \
                                     src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj
dotnet build src/Smx.Functions.sln
```
Expected: `Build succeeded`.

- [ ] **Step 7: Commit**

```bash
git add src/Smx.SearchProxy src/Smx.SearchProxy.Tests src/Smx.Functions.sln
git commit -m "feat(proxy): Smx.SearchProxy skeleton + options; cover count cannot be configured off"
```

---

## Task 3: StructuralGuard — the project-blind layer

The proxy cannot know that "Acme Bottling" is a client name (that knowledge lives in the orchestrator, Task 13). What it *can* do is reject anything **shaped** like an identifier. This is defence in depth, not the primary control.

**Files:**
- Create: `src/Smx.SearchProxy/Anonymity/StructuralGuard.cs`
- Test: `src/Smx.SearchProxy.Tests/StructuralGuardTests.cs`

- [ ] **Step 1: Write the failing test**

`src/Smx.SearchProxy.Tests/StructuralGuardTests.cs`:
```csharp
using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Tests;

public class StructuralGuardTests
{
    private static readonly StructuralGuard Guard = new(new ProxyOptions());

    private static SearchRequest Req(string query, string intent = SearchIntents.CandidateForms, int max = 10) =>
        new(query, intent, max);

    [Fact]
    public void CleanChemistryQuery_IsAllowed()
    {
        var v = Guard.Check(Req("ytterbium neodecanoate solubility in polyethylene"));
        Assert.True(v.Allowed);
        Assert.Null(v.Reason);
    }

    // A CAS number must survive the digit-run rule — hyphens break the run, which is why the rule is
    // \d{7,} and not "contains many digits". Rejecting CAS numbers would make the tool useless.
    [Fact]
    public void CasNumber_IsAllowed()
    {
        Assert.True(Guard.Check(Req("CAS 1314-36-9 yttrium oxide XRF")).Allowed);
    }

    [Theory]
    [InlineData("marker for 3f2504e0-4f89-11d3-9a0c-0305e82c3301", "contains_guid")]
    [InlineData("ask eli@tectika.com about the marker", "contains_email")]
    [InlineData("see https://internal.smx/projects/42", "contains_url")]
    [InlineData("visit www.acme-bottling.com marker", "contains_url")]
    [InlineData("purchase order 100045567788 marker", "contains_digit_run")]
    public void IdentifierShapedQueries_AreRejected(string query, string expectedReason)
    {
        var v = Guard.Check(Req(query));
        Assert.False(v.Allowed);
        Assert.Equal(expectedReason, v.Reason);
    }

    [Fact]
    public void EmptyQuery_IsRejected() =>
        Assert.Equal("query_empty", Guard.Check(Req("   ")).Reason);

    [Fact]
    public void OverLongQuery_IsRejected() =>
        Assert.Equal("query_too_long", Guard.Check(Req(new string('a', 257))).Reason);

    [Fact]
    public void UnknownIntent_IsRejected() =>
        Assert.Equal("unknown_intent", Guard.Check(Req("yttrium forms", intent: "regulatory.screen")).Reason);

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void MaxResultsOutOfRange_IsRejected(int max) =>
        Assert.Equal("max_results_out_of_range", Guard.Check(Req("yttrium forms", max: max)).Reason);
}
```

- [ ] **Step 2: Run it — it must fail**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter StructuralGuardTests
```
Expected: FAIL — `StructuralGuard` does not exist.

- [ ] **Step 3: Implement**

`src/Smx.SearchProxy/Anonymity/StructuralGuard.cs`:
```csharp
using System.Text.RegularExpressions;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Anonymity;

public sealed record GuardVerdict(bool Allowed, string? Reason)
{
    public static readonly GuardVerdict Ok = new(true, null);
    public static GuardVerdict Block(string reason) => new(false, reason);
}

/// Layer 2 of the anonymization (spec §6.2). PROJECT-BLIND: it holds no client names, no project ids, no
/// customer roster — putting that list in git on the internet-facing component would be the wrong trade in
/// the wrong place. It rejects strings SHAPED like identifiers. The layer that knows the actual names is
/// SensitiveTermGuard, in the orchestrator, where the names already live.
public sealed partial class StructuralGuard(ProxyOptions opts)
{
    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidLike();

    [GeneratedRegex(@"[^\s@]+@[^\s@]+\.[^\s@]{2,}")]
    private static partial Regex EmailLike();

    [GeneratedRegex(@"(https?://|www\.)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlLike();

    /// Seven or more CONSECUTIVE digits. A CAS number (1314-36-9) is hyphen-separated and survives; an order
    /// number, a phone number or a batch id does not.
    [GeneratedRegex(@"\d{7,}")]
    private static partial Regex DigitRun();

    public GuardVerdict Check(SearchRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Query)) return GuardVerdict.Block("query_empty");
        if (req.Query.Length > opts.MaxQueryChars) return GuardVerdict.Block("query_too_long");
        if (!SearchIntents.All.Contains(req.Intent)) return GuardVerdict.Block("unknown_intent");
        if (req.MaxResults < 1 || req.MaxResults > 20) return GuardVerdict.Block("max_results_out_of_range");

        if (GuidLike().IsMatch(req.Query)) return GuardVerdict.Block("contains_guid");
        if (EmailLike().IsMatch(req.Query)) return GuardVerdict.Block("contains_email");
        if (UrlLike().IsMatch(req.Query)) return GuardVerdict.Block("contains_url");
        if (DigitRun().IsMatch(req.Query)) return GuardVerdict.Block("contains_digit_run");

        return GuardVerdict.Ok;
    }
}
```

- [ ] **Step 4: Run — it must pass**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter StructuralGuardTests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.SearchProxy/Anonymity src/Smx.SearchProxy.Tests/StructuralGuardTests.cs
git commit -m "feat(proxy): StructuralGuard — reject identifier-shaped queries, keep CAS numbers usable"
```

---

## Task 4: The cover corpus + its offline generator

The decoys must be *chemically plausible siblings* of a real Discovery query. The proxy has **no Cosmos RBAC**, so it cannot read `ref-catalog` at runtime — it must carry its decoys with it, as git-versioned, PR-reviewed JSON. That constraint is a feature: the corpus is reviewable.

**Files:**
- Create: `tools/Smx.CoverCorpus/Smx.CoverCorpus.csproj`, `tools/Smx.CoverCorpus/Program.cs`
- Create: `src/Smx.SearchProxy/Config/cover-corpus.json` (generated)
- Create: `src/Smx.SearchProxy/Anonymity/CoverCorpus.cs`
- Test: `src/Smx.SearchProxy.Tests/CoverCorpusTests.cs`

- [ ] **Step 1: Write the generator**

`tools/Smx.CoverCorpus/Smx.CoverCorpus.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RollForward>Major</RollForward>
  </PropertyGroup>
</Project>
```

`tools/Smx.CoverCorpus/Program.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

// Generates src/Smx.SearchProxy/Config/cover-corpus.json from the seeded reference catalog.
//
// The decoys have to look like the real thing. A real Discovery query is a question about an element's
// molecular forms, their properties, or where to buy them — so the corpus is exactly that question, asked
// about every element and form in the catalog. The result is a chemically plausible haystack: Brave sees a
// stream of taggant-chemistry questions spanning the whole catalog and cannot tell which one a live project
// actually asked.
//
// Usage:
//   dotnet run --project tools/Smx.CoverCorpus -- \
//     src/Smx.Functions/Reference/Seed src/Smx.SearchProxy/Config/cover-corpus.json

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Smx.CoverCorpus <seed-dir> <output-json>");
    return 1;
}
var (seedDir, outPath) = (args[0], args[1]);

var elements = JsonNode.Parse(File.ReadAllText(Path.Combine(seedDir, "catalog-elements.json")))!.AsArray();
var products = JsonNode.Parse(File.ReadAllText(Path.Combine(seedDir, "catalog-products.json")))!.AsArray();

// (element symbol, group) — e.g. ("Y", "Rare earth")
var elementList = elements
    .Select(e => (Symbol: e!["element"]!.GetValue<string>(), Group: e["group"]?.GetValue<string>() ?? "marker"))
    .Distinct()
    .ToList();

// (element, molecular form) — e.g. ("Y", "2-ethylhexanoate"). Split the comma-separated `forms` cell.
var forms = elements
    .SelectMany(e => (e!["forms"]?.GetValue<string>() ?? "")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(f => f.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .Select(f => (Element: e["element"]!.GetValue<string>(), Form: f)))
    .Distinct()
    .ToList();

// (element, molecule) from the product listings — the most realistic decoys of all: real molecule names.
var molecules = products
    .Select(p => (Element: p!["element"]!.GetValue<string>(), Molecule: p["molecule"]!.GetValue<string>()))
    .Distinct()
    .ToList();

string[] substrates = ["polyethylene", "PET", "HDPE", "polypropylene", "paper label", "solvent ink", "glass"];

var candidateForms = elementList
    .SelectMany(e => new[]
    {
        $"{e.Symbol} marker molecular forms and CAS numbers",
        $"{e.Symbol} organometallic forms for XRF tagging",
        $"{e.Group} taggant candidates {e.Symbol} available forms",
    })
    .ToList();

var formProperties = forms
    .SelectMany(f => substrates.Take(3).Select(s => $"{f.Element} {f.Form} solubility and dispersion in {s}"))
    .Concat(molecules.Select(m => $"{m.Molecule} XRF detection limit and thermal stability"))
    .ToList();

var supplierAvailability = molecules
    .SelectMany(m => new[]
    {
        $"{m.Molecule} suppliers and purity grades",
        $"{m.Element} taggant precursor availability research quantities",
    })
    .ToList();

var corpus = new Dictionary<string, string[]>
{
    ["discovery.candidate_forms"] = candidateForms.Distinct().Order().ToArray(),
    ["discovery.form_properties"] = formProperties.Distinct().Order().ToArray(),
    ["discovery.supplier_availability"] = supplierAvailability.Distinct().Order().ToArray(),
};

File.WriteAllText(outPath, JsonSerializer.Serialize(corpus, new JsonSerializerOptions { WriteIndented = true }));
foreach (var (intent, qs) in corpus) Console.WriteLine($"{intent}: {qs.Length} decoys");
return 0;
```

- [ ] **Step 2: Generate the corpus**

```bash
dotnet run --project tools/Smx.CoverCorpus -- \
  src/Smx.Functions/Reference/Seed src/Smx.SearchProxy/Config/cover-corpus.json
```
Expected: three lines, each reporting **at least 20 decoys** (the catalog has 27 elements and 77 products, so expect roughly 80 / 200+ / 150+). If any family reports fewer than 20, the loader in Step 4 will refuse to start — fix the generator, do not lower the floor.

- [ ] **Step 3: Write the failing loader test**

`src/Smx.SearchProxy.Tests/CoverCorpusTests.cs`:
```csharp
using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Tests;

public class CoverCorpusTests
{
    private static string Json(int perFamily) =>
        "{" + string.Join(",", SearchIntents.All.Select(i =>
            $"\"{i}\":[" + string.Join(",", Enumerable.Range(0, perFamily).Select(n => $"\"decoy {i} {n}\"")) + "]")) + "}";

    [Fact]
    public void LoadsEveryIntentFamily()
    {
        var corpus = CoverCorpus.FromJson(Json(20));
        foreach (var intent in SearchIntents.All)
            Assert.Equal(20, corpus.For(intent).Count);
    }

    // A new intent must not be able to ship without its decoys: it would egress a real query naked, inside a
    // batch the proxy could not fill. Fail at startup, loudly, not at 3am on the first live query.
    [Fact]
    public void ThrowsWhenAnIntentHasNoFamily()
    {
        var missing = "{\"discovery.candidate_forms\":[\"a\",\"b\"]}";
        var ex = Assert.Throws<InvalidOperationException>(() => CoverCorpus.FromJson(missing));
        Assert.Contains("discovery.form_properties", ex.Message);
    }

    [Fact]
    public void ThrowsWhenAFamilyIsTooThinToHideAQuery()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CoverCorpus.FromJson(Json(3)));
        Assert.Contains("at least 20", ex.Message);
    }

    [Fact]
    public void TheShippedCorpusIsValid()
    {
        // The real artifact, loaded exactly as production loads it. If the generator regressed, this fails.
        var corpus = CoverCorpus.FromFile("Config/cover-corpus.json");
        foreach (var intent in SearchIntents.All)
            Assert.InRange(corpus.For(intent).Count, 20, int.MaxValue);
    }
}
```

For `TheShippedCorpusIsValid` to find the file, add to `src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj`:
```xml
  <ItemGroup>
    <None Include="../Smx.SearchProxy/Config/cover-corpus.json"
          Link="Config/cover-corpus.json"
          CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 4: Run — it must fail, then implement**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter CoverCorpusTests
```
Expected: FAIL — `CoverCorpus` does not exist.

`src/Smx.SearchProxy/Anonymity/CoverCorpus.cs`:
```csharp
using System.Text.Json;
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Anonymity;

/// The decoy pool, keyed by intent. Git-versioned and PR-reviewed, exactly like the SDS supplier allowlist
/// and the regulator registry — this is security-critical data, and a bad edit silently weakens the
/// anonymization rather than breaking anything visible.
///
/// It ships as a file rather than a Cosmos lookup because the proxy's identity has NO corpus RBAC and must
/// keep it (spec §2 D2). The constraint is load-bearing, not incidental.
public sealed class CoverCorpus
{
    /// Below this, a family is too thin to hide a query in: with ~20 decoys per family and 3 drawn per
    /// batch, an observer needs many rounds to distinguish signal from cover. It is a floor, not a target.
    public const int MinimumPerFamily = 20;

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _families;

    private CoverCorpus(IReadOnlyDictionary<string, IReadOnlyList<string>> families) => _families = families;

    public IReadOnlyList<string> For(string intent) => _families[intent];

    public static CoverCorpus FromFile(string path) => FromJson(File.ReadAllText(path));

    public static CoverCorpus FromJson(string json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json)
                  ?? throw new InvalidOperationException("cover corpus is empty or unparseable");

        var families = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var intent in SearchIntents.All)
        {
            if (!raw.TryGetValue(intent, out var qs))
                throw new InvalidOperationException(
                    $"cover corpus has no decoy family for intent '{intent}' — a real query for it would egress naked");
            if (qs.Count < MinimumPerFamily)
                throw new InvalidOperationException(
                    $"cover corpus family '{intent}' has {qs.Count} decoys; at least {MinimumPerFamily} are required to hide a query");
            families[intent] = qs;
        }
        return new CoverCorpus(families);
    }
}
```

- [ ] **Step 5: Run — it must pass**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter CoverCorpusTests
```
Expected: PASS, 4 tests.

- [ ] **Step 6: Commit**

```bash
git add tools/Smx.CoverCorpus src/Smx.SearchProxy/Config/cover-corpus.json \
        src/Smx.SearchProxy/Anonymity/CoverCorpus.cs src/Smx.SearchProxy.Tests
git commit -m "feat(proxy): cover corpus — a chemically plausible haystack, generated from the catalog

The decoys are the same questions a real Discovery query asks, asked about
every element and form in the seeded catalog. Ships as git-versioned JSON
because the proxy identity has no Cosmos RBAC and must keep it. The loader
refuses to start on a thin family: a new intent cannot ship without its cover."
```

---

## Task 5: CoverBatch — the k-anonymity mechanism

**Files:**
- Create: `src/Smx.SearchProxy/Anonymity/CoverBatch.cs`
- Test: `src/Smx.SearchProxy.Tests/CoverBatchTests.cs`

- [ ] **Step 1: Write the failing test**

`src/Smx.SearchProxy.Tests/CoverBatchTests.cs`:
```csharp
using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Tests;

public class CoverBatchTests
{
    /// Deterministic "shuffle": reverses, so tests can assert on position without flaking.
    private sealed class ReverseShuffler : IShuffler
    {
        public void Shuffle<T>(IList<T> items)
        {
            var copy = items.Reverse().ToList();
            for (var i = 0; i < items.Count; i++) items[i] = copy[i];
        }
    }

    private static CoverCorpus Corpus() => CoverCorpus.FromJson(
        "{" + string.Join(",", SearchIntents.All.Select(i =>
            $"\"{i}\":[" + string.Join(",", Enumerable.Range(0, 30).Select(n => $"\"{i} decoy {n}\"")) + "]")) + "}");

    private static CoverBatch Batch(int coverCount) =>
        new(Corpus(), new ProxyOptions { CoverCount = Math.Max(2, coverCount) }, new ReverseShuffler());

    private const string Real = "ytterbium neodecanoate solubility in polyethylene";

    [Fact]
    public void BatchContainsTheRealQueryExactlyOnce()
    {
        var batch = Batch(4).Build(Real, SearchIntents.CandidateForms);
        Assert.Equal(4, batch.Count);
        Assert.Single(batch, q => q == Real);
    }

    // Invariant 4: a real query never egresses alone.
    [Fact]
    public void BatchAlwaysCarriesAtLeastOneDecoy()
    {
        foreach (var n in new[] { 2, 3, 4, 8 })
        {
            var batch = Batch(n).Build(Real, SearchIntents.CandidateForms);
            Assert.True(batch.Count >= 2);
            Assert.True(batch.Count(q => q != Real) >= 1);
        }
    }

    [Fact]
    public void DecoysComeFromTheRequestedIntentFamily()
    {
        var batch = Batch(4).Build(Real, SearchIntents.SupplierAvailability);
        foreach (var decoy in batch.Where(q => q != Real))
            Assert.StartsWith(SearchIntents.SupplierAvailability, decoy);
    }

    [Fact]
    public void DecoysAreDistinctAndNeverEqualTheRealQuery()
    {
        var batch = Batch(6).Build(Real, SearchIntents.CandidateForms);
        Assert.Equal(batch.Count, batch.Distinct().Count());
    }

    // A real query pinned at index 0 in every batch would defeat the whole exercise: the observer just reads
    // the first one. It must be shuffled into the batch.
    [Fact]
    public void TheRealQueryIsNotAlwaysFirst()
    {
        var batch = Batch(4).Build(Real, SearchIntents.CandidateForms);
        Assert.NotEqual(0, batch.ToList().IndexOf(Real));
    }

    // If the real query happens to BE one of the corpus decoys, we must not send it twice — a duplicate is a
    // tell.
    [Fact]
    public void RealQueryMatchingADecoy_IsNotDuplicated()
    {
        var collision = $"{SearchIntents.CandidateForms} decoy 7";
        var batch = Batch(4).Build(collision, SearchIntents.CandidateForms);
        Assert.Equal(4, batch.Count);
        Assert.Single(batch, q => q == collision);
    }
}
```

- [ ] **Step 2: Run — it must fail**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter CoverBatchTests
```
Expected: FAIL — `CoverBatch` / `IShuffler` do not exist.

- [ ] **Step 3: Implement**

`src/Smx.SearchProxy/Anonymity/CoverBatch.cs`:
```csharp
using Smx.SearchProxy.Config;

namespace Smx.SearchProxy.Anonymity;

/// Injected so tests are deterministic and production is not.
public interface IShuffler
{
    void Shuffle<T>(IList<T> items);
}

public sealed class RandomShuffler : IShuffler
{
    public void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}

/// Layer 3 — the actual anonymization (spec §6.3). The real query is issued to the provider inside a
/// shuffled batch of chemically plausible decoys drawn from the same intent family, so the provider sees a
/// stream of taggant-chemistry questions spanning the catalog and cannot tell which one is the live project's.
///
/// Every query in the batch is real traffic and every result is cached (see SearchPipeline) — so the cover
/// is not waste. It warms the cache, and future real queries increasingly never egress at all.
public sealed class CoverBatch(CoverCorpus corpus, ProxyOptions opts, IShuffler shuffler)
{
    public IReadOnlyList<string> Build(string realQuery, string intent)
    {
        // opts.CoverCount is clamped to >= 2 in ProxyOptions.From; clamp again here so a hand-constructed
        // ProxyOptions in a test cannot accidentally send a naked query either.
        var size = Math.Max(2, opts.CoverCount);

        var pool = corpus.For(intent)
            .Where(q => !string.Equals(q, realQuery, StringComparison.OrdinalIgnoreCase))
            .ToList();
        shuffler.Shuffle(pool);

        var batch = new List<string>(size) { realQuery };
        batch.AddRange(pool.Take(size - 1));
        shuffler.Shuffle(batch);
        return batch;
    }
}
```

- [ ] **Step 4: Run — it must pass**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter CoverBatchTests
```
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.SearchProxy/Anonymity/CoverBatch.cs src/Smx.SearchProxy.Tests/CoverBatchTests.cs
git commit -m "feat(proxy): CoverBatch — k-anonymity, the mechanism that makes 'anonymizing' mean something"
```

---

## Task 6: ISearchProvider + BraveSearchProvider + the dry-run twin

**Files:**
- Create: `src/Smx.SearchProxy/Providers/ISearchProvider.cs`, `BraveSearchProvider.cs`, `DryRunSearchProvider.cs`
- Test: `src/Smx.SearchProxy.Tests/BraveSearchProviderTests.cs`

- [ ] **Step 1: Write the failing test**

`src/Smx.SearchProxy.Tests/BraveSearchProviderTests.cs`:
```csharp
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Providers;

namespace Smx.SearchProxy.Tests;

public class BraveSearchProviderTests
{
    /// Records every outgoing request and replies from a queued script.
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _script;
        public readonly List<HttpRequestMessage> Requests = [];
        public StubHandler(params HttpResponseMessage[] responses) => _script = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_script.Count > 0 ? _script.Dequeue() : new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private const string BraveJson = """
    {
      "web": {
        "results": [
          {
            "title": "Yttrium 2-ethylhexanoate",
            "url": "https://pubchem.ncbi.nlm.nih.gov/compound/12345",
            "description": "CAS 80326-98-3, soluble in aliphatic solvents.",
            "age": "2024-03-01",
            "meta_url": { "hostname": "pubchem.ncbi.nlm.nih.gov" }
          }
        ]
      }
    }
    """;

    private static BraveSearchProvider Provider(StubHandler handler, int retries = 3) =>
        new(new HttpClient(handler), new ProxyOptions { ApiKey = "test-key", Retries = retries }, NullLogger<BraveSearchProvider>.Instance);

    [Fact]
    public async Task NormalizesTheSerpJson()
    {
        var handler = new StubHandler(Ok(BraveJson));
        var hits = await Provider(handler).SearchAsync("yttrium forms", 10, null, default);

        Assert.NotNull(hits);
        var hit = Assert.Single(hits);
        Assert.Equal("Yttrium 2-ethylhexanoate", hit.Title);
        Assert.Equal("https://pubchem.ncbi.nlm.nih.gov/compound/12345", hit.Url);
        Assert.Contains("80326-98-3", hit.Snippet);
        Assert.Equal("pubchem.ncbi.nlm.nih.gov", hit.Host);
        Assert.Equal("2024-03-01", hit.Age);
    }

    // Request hygiene (spec §6.4). Anything we send that identifies us or lets Brave correlate our requests
    // is a handle we gave away for free.
    [Fact]
    public async Task SendsOnlyTheApiKeyAndAccept_NoCookieReferrerOrTraceHeader()
    {
        var handler = new StubHandler(Ok(BraveJson));
        await Provider(handler).SearchAsync("yttrium forms", 10, null, default);

        var req = Assert.Single(handler.Requests);
        Assert.True(req.Headers.Contains("X-Subscription-Token"));
        Assert.False(req.Headers.Contains("Cookie"));
        Assert.False(req.Headers.Contains("Referer"));
        Assert.False(req.Headers.Contains("traceparent"));
        Assert.False(req.Headers.Contains("Request-Id"));
        Assert.Null(req.Headers.UserAgent.FirstOrDefault());
    }

    [Fact]
    public async Task Retries5xxThenSucceeds()
    {
        var handler = new StubHandler(Status(HttpStatusCode.BadGateway), Ok(BraveJson));
        var hits = await Provider(handler).SearchAsync("yttrium forms", 10, null, default);
        Assert.NotNull(hits);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Retries429()
    {
        var handler = new StubHandler(Status(HttpStatusCode.TooManyRequests), Ok(BraveJson));
        var hits = await Provider(handler).SearchAsync("yttrium forms", 10, null, default);
        Assert.NotNull(hits);
        Assert.Equal(2, handler.Requests.Count);
    }

    // A 401 (bad key) or a 400 (bad query) will never succeed on retry. Retrying just burns quota.
    [Fact]
    public async Task DoesNotRetry4xx()
    {
        var handler = new StubHandler(Status(HttpStatusCode.Unauthorized), Ok(BraveJson));
        var hits = await Provider(handler).SearchAsync("yttrium forms", 10, null, default);
        Assert.Null(hits);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ExhaustedRetries_ReturnsNullRatherThanThrowing()
    {
        var handler = new StubHandler(Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.BadGateway));
        var hits = await Provider(handler).SearchAsync("yttrium forms", 10, null, default);
        Assert.Null(hits);
    }

    // Invariant 1: exactly one upstream host, ever.
    [Fact]
    public void TargetsOnlyTheBraveApiHost()
    {
        Assert.Equal("api.search.brave.com", BraveSearchProvider.ApiHost);
    }

    [Fact]
    public async Task EmptySerp_ReturnsEmptyList_NotNull()
    {
        var hits = await Provider(new StubHandler(Ok("""{"web":{"results":[]}}"""))).SearchAsync("x", 10, null, default);
        Assert.NotNull(hits);
        Assert.Empty(hits);
    }
}
```

- [ ] **Step 2: Run — it must fail**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter BraveSearchProviderTests
```
Expected: FAIL — `BraveSearchProvider` does not exist.

- [ ] **Step 3: Implement the interface + the dry-run twin**

`src/Smx.SearchProxy/Providers/ISearchProvider.cs`:
```csharp
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Providers;

/// THE single outbound path of this app. Following the house convention (Sds/Sourcing/IEgressClient,
/// Reg/Sourcing/IRegEgress): a failure is `null`, never an exception — except OperationCanceledException,
/// which is rethrown. An empty list means "the provider answered, and found nothing"; null means "the
/// provider did not answer". The pipeline maps those to 200-with-no-results and 502 respectively, and the
/// difference matters: one is evidence of absence, the other is absence of evidence.
///
/// There is deliberately NO FetchAsync(Uri) here. See spec §3, invariant 2 — the absence of a fetch
/// interface is why third-party hosts never see our IP.
public interface ISearchProvider
{
    Task<IReadOnlyList<SearchHit>?> SearchAsync(string query, int maxResults, int? freshnessDays, CancellationToken ct);
}
```

`src/Smx.SearchProxy/Providers/DryRunSearchProvider.cs`:
```csharp
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Providers;

/// PROXY_DRY_RUN=true. Zero egress, no API key needed — the whole app, including the cover batch and the
/// cache, runs end to end. Same idiom as DryRunEgressClient / RegDryRunEgress.
public sealed class DryRunSearchProvider(Func<string, IReadOnlyList<SearchHit>?>? responder = null) : ISearchProvider
{
    private readonly Func<string, IReadOnlyList<SearchHit>?> _responder = responder ?? Default;

    public Task<IReadOnlyList<SearchHit>?> SearchAsync(string query, int maxResults, int? freshnessDays, CancellationToken ct) =>
        Task.FromResult(_responder(query));

    private static IReadOnlyList<SearchHit>? Default(string query) =>
    [
        new SearchHit(
            Title: $"[dry-run] {query}",
            Url: "https://example.invalid/dry-run",
            Snippet: "Dry-run result — PROXY_DRY_RUN=true, nothing left the building.",
            Host: "example.invalid",
            Age: null),
    ];
}
```

- [ ] **Step 4: Implement the Brave provider**

`src/Smx.SearchProxy/Providers/BraveSearchProvider.cs`:
```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Providers;

/// The one component in this system that talks to the public internet at agent time.
///
/// Bing Search v7 was retired 2025-08-11 (410 Gone) and its replacement, Grounding with Bing, requires the
/// Foundry Agent Service this project cut. Brave runs its own index, so we are not proxying Google or Bing,
/// and its privacy positioning matches the claim we make to the client.
public sealed class BraveSearchProvider(HttpClient http, ProxyOptions opts, ILogger<BraveSearchProvider> log) : ISearchProvider
{
    /// Invariant 1 (spec §3): exactly one upstream host, ever. An allowlist of one.
    public const string ApiHost = "api.search.brave.com";
    private const string ApiPath = "/res/v1/web/search";

    public async Task<IReadOnlyList<SearchHit>?> SearchAsync(string query, int maxResults, int? freshnessDays, CancellationToken ct)
    {
        var url = new UriBuilder("https", ApiHost) { Path = ApiPath }.Uri;
        var qs = $"?q={Uri.EscapeDataString(query)}&count={Math.Clamp(maxResults, 1, 20)}";
        if (freshnessDays is > 0) qs += $"&freshness=pd{freshnessDays}";
        var target = new Uri(url, ApiPath + qs);

        if (target.Host != ApiHost)
        {
            log.LogError("Provider egress blocked: host {Host} is not the single allowed upstream", target.Host);
            return null;
        }

        var attempts = Math.Max(1, opts.Retries);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                // A FRESH request each attempt, carrying ONLY what Brave needs. No cookies, no referrer, no
                // caller identity. The W3C traceparent header that HttpClient would otherwise inject is
                // suppressed at the handler (see Program.cs) — it would be a correlation handle handed to
                // Brave for free, and it would defeat the cover batch by grouping the N queries as one trace.
                using var req = new HttpRequestMessage(HttpMethod.Get, target);
                req.Headers.TryAddWithoutValidation("X-Subscription-Token", opts.ApiKey);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                var transient = (int)resp.StatusCode >= 500 || resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
                if (transient && attempt < attempts)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt * 2);
                    log.LogWarning("Brave → {Status}, retry {Attempt}/{Max} after {Wait}s", (int)resp.StatusCode, attempt, attempts, wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    // 4xx is permanent: a bad key or a malformed query will not heal on retry, it only burns quota.
                    log.LogWarning("Brave → {Status}; giving up", (int)resp.StatusCode);
                    return null;
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > opts.MaxResponseBytes)
                {
                    log.LogWarning("Brave response oversize ({Len} bytes)", bytes.Length);
                    return null;
                }
                return Parse(bytes);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < attempts)
            {
                log.LogWarning(ex, "Brave attempt {Attempt}/{Max} failed", attempt, attempts);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Brave search failed");
                return null;
            }
        }
        return null;
    }

    private static IReadOnlyList<SearchHit> Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("web", out var web) ||
            !web.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            return [];

        var hits = new List<SearchHit>();
        foreach (var r in results.EnumerateArray())
        {
            var url = Str(r, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            var host = r.TryGetProperty("meta_url", out var meta) ? Str(meta, "hostname") : null;
            hits.Add(new SearchHit(
                Title: Str(r, "title") ?? url,
                Url: url,
                Snippet: Str(r, "description") ?? "",
                Host: host ?? SafeHost(url),
                Age: Str(r, "age")));
        }
        return hits;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string SafeHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
}
```

- [ ] **Step 5: Run — it must pass**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter BraveSearchProviderTests
```
Expected: PASS, 8 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.SearchProxy/Providers src/Smx.SearchProxy.Tests/BraveSearchProviderTests.cs
git commit -m "feat(proxy): Brave provider — one upstream host, no fetch interface, no correlation headers"
```

---

## Task 7: The result cache

Content-addressed, TTL-bounded, in the proxy's **own** storage account (it already holds Blob Data Owner there — no new RBAC). Decoy results are cached alongside the real one, so the cover traffic warms the cache instead of being pure waste.

**Files:**
- Create: `src/Smx.SearchProxy/Pipeline/CacheKey.cs`, `ISearchCache.cs`, `BlobSearchCache.cs`
- Create: `src/Smx.SearchProxy.Tests/Fakes/InMemorySearchCache.cs`
- Test: `src/Smx.SearchProxy.Tests/CacheTests.cs`

- [ ] **Step 1: Write the failing test**

`src/Smx.SearchProxy.Tests/CacheTests.cs`:
```csharp
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Pipeline;
using Smx.SearchProxy.Tests.Fakes;

namespace Smx.SearchProxy.Tests;

public class CacheTests
{
    private static readonly IReadOnlyList<SearchHit> Hits =
        [new SearchHit("t", "https://example.org/a", "s", "example.org", null)];

    [Fact]
    public void Key_IsStableUnderWhitespaceAndCase()
    {
        Assert.Equal(
            CacheKey.For("Yttrium   Neodecanoate ", SearchIntents.CandidateForms, 10),
            CacheKey.For("yttrium neodecanoate", SearchIntents.CandidateForms, 10));
    }

    [Fact]
    public void Key_DiffersByIntentAndMaxResults()
    {
        var a = CacheKey.For("q", SearchIntents.CandidateForms, 10);
        Assert.NotEqual(a, CacheKey.For("q", SearchIntents.FormProperties, 10));
        Assert.NotEqual(a, CacheKey.For("q", SearchIntents.CandidateForms, 5));
    }

    [Fact]
    public async Task RoundTripsWithinTtl()
    {
        var cache = new InMemorySearchCache(ttlHours: 168);
        await cache.SetAsync("k", Hits, "2026-07-13T10:00:00Z", default);
        var got = await cache.GetAsync("k", "2026-07-14T10:00:00Z", default);
        Assert.NotNull(got);
        Assert.Equal("https://example.org/a", got![0].Url);
    }

    [Fact]
    public async Task ExpiredEntry_IsAMiss()
    {
        var cache = new InMemorySearchCache(ttlHours: 168);
        await cache.SetAsync("k", Hits, "2026-07-01T10:00:00Z", default);
        Assert.Null(await cache.GetAsync("k", "2026-07-13T10:00:00Z", default)); // 12 days > 7
    }

    [Fact]
    public async Task UnknownKey_IsAMiss() =>
        Assert.Null(await new InMemorySearchCache(168).GetAsync("nope", "2026-07-13T10:00:00Z", default));
}
```

`src/Smx.SearchProxy.Tests/Fakes/InMemorySearchCache.cs`:
```csharp
using System.Globalization;
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Pipeline;

namespace Smx.SearchProxy.Tests.Fakes;

public sealed class InMemorySearchCache(int ttlHours) : ISearchCache
{
    private readonly Dictionary<string, (string FetchedAt, IReadOnlyList<SearchHit> Hits)> _store = [];
    public int Writes { get; private set; }

    public Task<IReadOnlyList<SearchHit>?> GetAsync(string key, string nowUtc, CancellationToken ct)
    {
        if (!_store.TryGetValue(key, out var e)) return Task.FromResult<IReadOnlyList<SearchHit>?>(null);
        var fetchedAt = DateTimeOffset.Parse(e.FetchedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var now = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var fresh = now - fetchedAt < TimeSpan.FromHours(ttlHours);
        return Task.FromResult(fresh ? e.Hits : null);
    }

    public Task SetAsync(string key, IReadOnlyList<SearchHit> hits, string nowUtc, CancellationToken ct)
    {
        _store[key] = (nowUtc, hits);
        Writes++;
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run — it must fail**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter CacheTests
```
Expected: FAIL — `CacheKey` / `ISearchCache` do not exist.

- [ ] **Step 3: Implement**

`src/Smx.SearchProxy/Pipeline/CacheKey.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Smx.SearchProxy.Pipeline;

public static partial class CacheKey
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// Content-addressed. The key is a hash, not the query text — so the cache blob names in storage do not
    /// themselves become a readable log of what we searched for.
    public static string For(string query, string intent, int maxResults)
    {
        var normalized = Whitespace().Replace(query.Trim().ToLowerInvariant(), " ");
        var material = $"{intent}|{maxResults}|{normalized}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
```

`src/Smx.SearchProxy/Pipeline/ISearchCache.cs`:
```csharp
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Pipeline;

/// `nowUtc` is passed in (never DateTime.UtcNow inside) so TTL behaviour is deterministic in tests — the
/// same convention as SdsSweep.RunSweepAsync / SyncPipeline.RunSyncAsync.
public interface ISearchCache
{
    Task<IReadOnlyList<SearchHit>?> GetAsync(string key, string nowUtc, CancellationToken ct);
    Task SetAsync(string key, IReadOnlyList<SearchHit> hits, string nowUtc, CancellationToken ct);
}
```

`src/Smx.SearchProxy/Pipeline/BlobSearchCache.cs`:
```csharp
using System.Globalization;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Pipeline;

/// Lives in the proxy's OWN storage account, on which its identity already holds Blob Data Owner
/// (infra/modules/functions.bicep:160-171). No new RBAC — and in particular no Cosmos, no Bronze, no AI
/// Search. The blast radius of this app must stay exactly where it is.
public sealed class BlobSearchCache(BlobContainerClient container, ProxyOptions opts, ILogger<BlobSearchCache> log) : ISearchCache
{
    private sealed record Entry(string FetchedAt, IReadOnlyList<SearchHit> Hits);

    public async Task<IReadOnlyList<SearchHit>?> GetAsync(string key, string nowUtc, CancellationToken ct)
    {
        try
        {
            var resp = await container.GetBlobClient($"{key}.json").DownloadContentAsync(ct);
            var entry = JsonSerializer.Deserialize<Entry>(resp.Value.Content.ToString());
            if (entry is null) return null;

            var fetchedAt = DateTimeOffset.Parse(entry.FetchedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var now = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            return now - fetchedAt < TimeSpan.FromHours(opts.CacheTtlHours) ? entry.Hits : null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null; // an ordinary miss
        }
        catch (Exception ex)
        {
            // A broken cache must never fail a search — it degrades to an egress, which is correct behaviour.
            log.LogWarning(ex, "Search cache read failed for {Key}; treating as a miss", key);
            return null;
        }
    }

    public async Task SetAsync(string key, IReadOnlyList<SearchHit> hits, string nowUtc, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(new Entry(nowUtc, hits));
            await container.GetBlobClient($"{key}.json").UploadAsync(BinaryData.FromString(json), overwrite: true, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Search cache write failed for {Key}", key);
        }
    }
}
```

- [ ] **Step 4: Run — it must pass, then commit**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter CacheTests
git add src/Smx.SearchProxy/Pipeline src/Smx.SearchProxy.Tests
git commit -m "feat(proxy): content-addressed result cache — decoys warm it, so cover traffic is not waste"
```
Expected: PASS, 5 tests.

---

## Task 8: QuotaGuard — a runaway loop must be a 429, not an invoice

**Files:**
- Create: `src/Smx.SearchProxy/Pipeline/IQuotaStore.cs`, `BlobQuotaStore.cs`, `QuotaGuard.cs`
- Create: `src/Smx.SearchProxy.Tests/Fakes/InMemoryQuotaStore.cs`
- Test: `src/Smx.SearchProxy.Tests/QuotaGuardTests.cs`

- [ ] **Step 1: Write the failing test**

`src/Smx.SearchProxy.Tests/QuotaGuardTests.cs`:
```csharp
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Pipeline;
using Smx.SearchProxy.Tests.Fakes;

namespace Smx.SearchProxy.Tests;

public class QuotaGuardTests
{
    private static QuotaGuard Guard(int monthlyCap, int perMinute, InMemoryQuotaStore store) =>
        new(store, new ProxyOptions { MonthlyQueryCap = monthlyCap, RateLimitPerMinute = perMinute });

    [Fact]
    public async Task AllowsUntilTheMonthlyCap_ThenRefuses()
    {
        var store = new InMemoryQuotaStore();
        var guard = Guard(monthlyCap: 10, perMinute: 1000, store);

        Assert.True(await guard.TryConsumeAsync(4, "2026-07-13T10:00:00Z", default));
        Assert.True(await guard.TryConsumeAsync(4, "2026-07-13T10:01:00Z", default));
        // 8 spent; a 4-query batch would reach 12 > 10.
        Assert.False(await guard.TryConsumeAsync(4, "2026-07-13T10:02:00Z", default));
    }

    // The cap is on PROVIDER CALLS, decoys included — that is what Brave bills for.
    [Fact]
    public async Task TheCapCountsDecoysNotJustRealQueries()
    {
        var store = new InMemoryQuotaStore();
        var guard = Guard(monthlyCap: 4, perMinute: 1000, store);
        Assert.True(await guard.TryConsumeAsync(4, "2026-07-13T10:00:00Z", default));
        Assert.Equal(4, store.CountFor("2026-07"));
        Assert.False(await guard.TryConsumeAsync(1, "2026-07-13T10:00:01Z", default));
    }

    [Fact]
    public async Task TheCapResetsWithTheMonth()
    {
        var store = new InMemoryQuotaStore();
        var guard = Guard(monthlyCap: 4, perMinute: 1000, store);
        Assert.True(await guard.TryConsumeAsync(4, "2026-07-31T23:59:00Z", default));
        Assert.True(await guard.TryConsumeAsync(4, "2026-08-01T00:00:00Z", default));
    }

    [Fact]
    public async Task RateLimit_RefusesABurstWithinTheSameMinute()
    {
        var guard = Guard(monthlyCap: 10_000, perMinute: 5, new InMemoryQuotaStore());
        Assert.True(await guard.TryConsumeAsync(4, "2026-07-13T10:00:00Z", default));
        Assert.False(await guard.TryConsumeAsync(4, "2026-07-13T10:00:30Z", default)); // 8 > 5 in the minute
        Assert.True(await guard.TryConsumeAsync(4, "2026-07-13T10:01:00Z", default));  // new minute
    }
}
```

`src/Smx.SearchProxy.Tests/Fakes/InMemoryQuotaStore.cs`:
```csharp
using Smx.SearchProxy.Pipeline;

namespace Smx.SearchProxy.Tests.Fakes;

public sealed class InMemoryQuotaStore : IQuotaStore
{
    private readonly Dictionary<string, int> _months = [];

    public int CountFor(string month) => _months.GetValueOrDefault(month);

    public Task<int> ReadAsync(string month, CancellationToken ct) =>
        Task.FromResult(_months.GetValueOrDefault(month));

    public Task<int> AddAsync(string month, int delta, CancellationToken ct)
    {
        var next = _months.GetValueOrDefault(month) + delta;
        _months[month] = next;
        return Task.FromResult(next);
    }
}
```

- [ ] **Step 2: Run — it must fail**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter QuotaGuardTests
```
Expected: FAIL — `QuotaGuard` / `IQuotaStore` do not exist.

- [ ] **Step 3: Implement**

`src/Smx.SearchProxy/Pipeline/IQuotaStore.cs`:
```csharp
namespace Smx.SearchProxy.Pipeline;

/// The monthly provider-call counter. Persistent because Flex Consumption recycles instances and a counter
/// that resets on cold start is not a cap.
public interface IQuotaStore
{
    Task<int> ReadAsync(string month, CancellationToken ct);
    /// Returns the new total.
    Task<int> AddAsync(string month, int delta, CancellationToken ct);
}
```

`src/Smx.SearchProxy/Pipeline/QuotaGuard.cs`:
```csharp
using System.Globalization;
using Smx.SearchProxy.Config;

namespace Smx.SearchProxy.Pipeline;

/// Two bounds, both on PROVIDER CALLS (decoys included — that is what the bill and the egress log count):
///   • a monthly cap, so a runaway agent loop is a 429 rather than an invoice;
///   • a per-minute bucket, so a burst cannot spray egress even inside the cap.
/// Both are deliberately crude. They are a backstop against our own bugs, not a billing system.
public sealed class QuotaGuard(IQuotaStore store, ProxyOptions opts)
{
    private readonly Lock _gate = new();
    private string _minute = "";
    private int _minuteCount;

    public async Task<bool> TryConsumeAsync(int providerCalls, string nowUtc, CancellationToken ct)
    {
        var now = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        lock (_gate)
        {
            var minute = now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
            if (minute != _minute) { _minute = minute; _minuteCount = 0; }
            if (_minuteCount + providerCalls > opts.RateLimitPerMinute) return false;
            _minuteCount += providerCalls;
        }

        var month = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var spent = await store.ReadAsync(month, ct);
        if (spent + providerCalls > opts.MonthlyQueryCap) return false;

        await store.AddAsync(month, providerCalls, ct);
        return true;
    }
}
```

> **Note on `Lock`:** `System.Threading.Lock` is .NET 9+. On **net8.0**, use `private readonly object _gate = new();` — the `lock (_gate)` statement is unchanged. Write it with `object`.

`src/Smx.SearchProxy/Pipeline/BlobQuotaStore.cs`:
```csharp
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace Smx.SearchProxy.Pipeline;

/// One small blob per month, updated with an optimistic-concurrency (ETag) compare-and-swap so two warm
/// instances cannot both read 4,999 and both spend.
public sealed class BlobQuotaStore(BlobContainerClient container, ILogger<BlobQuotaStore> log) : IQuotaStore
{
    private sealed record Counter(int Count);

    public async Task<int> ReadAsync(string month, CancellationToken ct)
    {
        var (count, _) = await LoadAsync(month, ct);
        return count;
    }

    public async Task<int> AddAsync(string month, int delta, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var (count, etag) = await LoadAsync(month, ct);
            var next = count + delta;
            var json = BinaryData.FromString(JsonSerializer.Serialize(new Counter(next)));
            var conditions = etag is null
                ? new BlobRequestConditions { IfNoneMatch = ETag.All }   // create-if-absent
                : new BlobRequestConditions { IfMatch = etag };          // update-if-unchanged
            try
            {
                await container.GetBlobClient(Name(month))
                    .UploadAsync(json, new BlobUploadOptions { Conditions = conditions }, ct);
                return next;
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                // Another instance won the race; re-read and try again.
            }
        }
        // Failing OPEN here would silently uncap spend. Fail closed: the caller sees "quota unavailable"
        // and returns 429, which is the safe direction for both the bill and the egress volume.
        log.LogError("Quota CAS failed for {Month} after 5 attempts", month);
        throw new InvalidOperationException($"quota store contention for {month}");
    }

    private static string Name(string month) => $"quota/{month}.json";

    private async Task<(int Count, ETag? ETag)> LoadAsync(string month, CancellationToken ct)
    {
        try
        {
            var resp = await container.GetBlobClient(Name(month)).DownloadContentAsync(ct);
            var counter = JsonSerializer.Deserialize<Counter>(resp.Value.Content.ToString());
            return (counter?.Count ?? 0, resp.Value.Details.ETag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return (0, null);
        }
    }
}
```

- [ ] **Step 4: Run — it must pass, then commit**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter QuotaGuardTests
git add src/Smx.SearchProxy/Pipeline src/Smx.SearchProxy.Tests
git commit -m "feat(proxy): QuotaGuard — a runaway agent loop is a 429, not an invoice"
```
Expected: PASS, 4 tests.

---

## Task 9: SearchPipeline + EgressAudit — the testable core

This is where every invariant meets. Read spec §3 again before writing it.

**Files:**
- Create: `src/Smx.SearchProxy/Pipeline/EgressAudit.cs`, `SearchPipeline.cs`
- Test: `src/Smx.SearchProxy.Tests/SearchPipelineTests.cs`

- [ ] **Step 1: Write the failing test**

`src/Smx.SearchProxy.Tests/SearchPipelineTests.cs`:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Pipeline;
using Smx.SearchProxy.Providers;
using Smx.SearchProxy.Tests.Fakes;

namespace Smx.SearchProxy.Tests;

public class SearchPipelineTests
{
    /// Records every query it is asked for, so the tests can assert on what would actually have egressed.
    private sealed class RecordingProvider(Func<string, IReadOnlyList<SearchHit>?>? responder = null) : ISearchProvider
    {
        public readonly List<string> Queries = [];
        public Task<IReadOnlyList<SearchHit>?> SearchAsync(string query, int maxResults, int? freshnessDays, CancellationToken ct)
        {
            lock (Queries) Queries.Add(query);
            var r = responder ?? (q => new[] { new SearchHit($"title {q}", $"https://example.org/{Uri.EscapeDataString(q)}", "snippet", "example.org", null) });
            return Task.FromResult(r(query));
        }
    }

    private sealed class Harness
    {
        public readonly RecordingProvider Provider;
        public readonly InMemorySearchCache Cache = new(168);
        public readonly InMemoryQuotaStore Quota = new();
        public readonly SearchPipeline Pipeline;

        public Harness(int coverCount = 4, int monthlyCap = 10_000, RecordingProvider? provider = null)
        {
            Provider = provider ?? new RecordingProvider();
            var opts = new ProxyOptions
            {
                CoverCount = coverCount, MonthlyQueryCap = monthlyCap, RateLimitPerMinute = 1000, ApiKey = "k",
            };
            var corpus = CoverCorpus.FromJson(
                "{" + string.Join(",", SearchIntents.All.Select(i =>
                    $"\"{i}\":[" + string.Join(",", Enumerable.Range(0, 30).Select(n => $"\"{i} decoy {n}\"")) + "]")) + "}");

            Pipeline = new SearchPipeline(
                new StructuralGuard(opts),
                new QuotaGuard(Quota, opts),
                Cache,
                new CoverBatch(corpus, opts, new RandomShuffler()),
                Provider,
                new EgressAudit(NullLogger<EgressAudit>.Instance),
                opts,
                NullLogger<SearchPipeline>.Instance);
        }
    }

    private const string Now = "2026-07-13T10:00:00Z";
    private static SearchRequest Req(string q = "ytterbium neodecanoate solubility") => new(q, SearchIntents.CandidateForms, 10);

    [Fact]
    public async Task HappyPath_ReturnsOnlyTheRealQuerysResults()
    {
        var h = new Harness();
        var result = await h.Pipeline.RunAsync(Req(), Now, default);

        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.Response);
        Assert.False(result.Response!.CacheHit);
        Assert.Equal(4, result.Response.CoverCount);
        // The decoys' results must NOT leak into the response — the caller sees only what it asked for.
        Assert.All(result.Response.Results, hit => Assert.Contains("ytterbium", hit.Title));
    }

    // Invariant 4. This is the test that the anonymization is actually happening.
    [Fact]
    public async Task TheRealQueryEgressesInsideABatchOfDecoys()
    {
        var h = new Harness(coverCount: 4);
        await h.Pipeline.RunAsync(Req(), Now, default);

        Assert.Equal(4, h.Provider.Queries.Count);
        Assert.Single(h.Provider.Queries, q => q == "ytterbium neodecanoate solubility");
        Assert.Equal(3, h.Provider.Queries.Count(q => q.StartsWith(SearchIntents.CandidateForms)));
    }

    // The decoys are not waste: their results are cached, so the NEXT real query that happens to match one
    // never egresses at all.
    [Fact]
    public async Task DecoyResultsAreCached()
    {
        var h = new Harness(coverCount: 4);
        await h.Pipeline.RunAsync(Req(), Now, default);
        Assert.Equal(4, h.Cache.Writes);
    }

    [Fact]
    public async Task CacheHit_EgressesNothing()
    {
        var h = new Harness();
        await h.Pipeline.RunAsync(Req(), Now, default);
        var before = h.Provider.Queries.Count;

        var second = await h.Pipeline.RunAsync(Req(), "2026-07-13T11:00:00Z", default);

        Assert.True(second.Response!.CacheHit);
        Assert.Equal(0, second.Response.CoverCount);
        Assert.Equal(before, h.Provider.Queries.Count); // not one more call
    }

    [Fact]
    public async Task BlockedQuery_Is400_AndNeverEgresses()
    {
        var h = new Harness();
        var result = await h.Pipeline.RunAsync(
            new SearchRequest("marker for 3f2504e0-4f89-11d3-9a0c-0305e82c3301", SearchIntents.CandidateForms), Now, default);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("contains_guid", result.Reason);
        Assert.Empty(h.Provider.Queries);
    }

    [Fact]
    public async Task QuotaExceeded_Is429_AndNeverEgresses()
    {
        var h = new Harness(coverCount: 4, monthlyCap: 2);
        var result = await h.Pipeline.RunAsync(Req(), Now, default);

        Assert.Equal(429, result.StatusCode);
        Assert.Equal("quota_exceeded", result.Reason);
        Assert.Empty(h.Provider.Queries);
    }

    // Absence of evidence is not evidence of absence: a provider failure must NOT look like "no results".
    // An agent that reads an empty list as "nothing exists" would draw a false conclusion.
    [Fact]
    public async Task ProviderFailureOnTheRealQuery_Is502_NotAnEmptyResultSet()
    {
        var h = new Harness(provider: new RecordingProvider(q => q.StartsWith("ytterbium") ? null : []));
        var result = await h.Pipeline.RunAsync(Req(), Now, default);

        Assert.Equal(502, result.StatusCode);
        Assert.Null(result.Response);
    }

    // A decoy failing is irrelevant — nobody consumes its results. It must not fail the real query.
    [Fact]
    public async Task DecoyFailure_DoesNotFailTheRealQuery()
    {
        var h = new Harness(provider: new RecordingProvider(q =>
            q.StartsWith(SearchIntents.CandidateForms) ? null : [new SearchHit("t", "https://example.org/a", "s", "example.org", null)]));
        var result = await h.Pipeline.RunAsync(Req(), Now, default);

        Assert.Equal(200, result.StatusCode);
        Assert.Single(result.Response!.Results);
    }

    [Fact]
    public async Task ProviderNotConfigured_Is503()
    {
        var opts = new ProxyOptions { ApiKey = "", DryRun = false, CoverCount = 4, RateLimitPerMinute = 100, MonthlyQueryCap = 100 };
        var corpus = CoverCorpus.FromJson(
            "{" + string.Join(",", SearchIntents.All.Select(i =>
                $"\"{i}\":[" + string.Join(",", Enumerable.Range(0, 30).Select(n => $"\"{i} d{n}\"")) + "]")) + "}");
        var pipeline = new SearchPipeline(
            new StructuralGuard(opts), new QuotaGuard(new InMemoryQuotaStore(), opts), new InMemorySearchCache(168),
            new CoverBatch(corpus, opts, new RandomShuffler()), new RecordingProvider(),
            new EgressAudit(NullLogger<EgressAudit>.Instance), opts, NullLogger<SearchPipeline>.Instance);

        var result = await pipeline.RunAsync(Req(), Now, default);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("provider_not_configured", result.Reason);
    }
}
```

- [ ] **Step 2: Run — it must fail**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter SearchPipelineTests
```
Expected: FAIL — `SearchPipeline` / `EgressAudit` do not exist.

- [ ] **Step 3: Implement `EgressAudit`**

`src/Smx.SearchProxy/Pipeline/EgressAudit.cs`:
```csharp
using Microsoft.Extensions.Logging;
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Pipeline;

/// The audit trail that turns "anonymizing" from a claim into something the operator can PROVE.
///
/// One structured event per request, under a single message template, so one KQL query answers "show me
/// everything that left the building":
///
///   traces | where message startswith "SearchProxyAudit"
///         | project timestamp, customDimensions.Decision, customDimensions.Intent,
///                   customDimensions.Query, customDimensions.CoverCount
///
/// The query text is logged deliberately. The audit is worthless without it, and it is safe: this component
/// is project-blind, so its log cannot correlate a query back to a project. Log Analytics is private.
public sealed class EgressAudit(ILogger<EgressAudit> log)
{
    private const string Template =
        "SearchProxyAudit decision={Decision} intent={Intent} query={Query} cover={CoverCount} results={ResultCount} reason={Reason}";

    public void Allowed(SearchRequest req, int coverCount, int resultCount) =>
        log.LogInformation(Template, "allowed", req.Intent, req.Query, coverCount, resultCount, "");

    public void CacheHit(SearchRequest req, int resultCount) =>
        log.LogInformation(Template, "cache_hit", req.Intent, req.Query, 0, resultCount, "");

    /// A blocked query is the MOST interesting line in the log: it is the system catching an attempt — by
    /// our own agent — to send something it should not have.
    public void Blocked(SearchRequest req, string reason) =>
        log.LogWarning(Template, "blocked", req.Intent, req.Query, 0, 0, reason);

    public void ProviderFailed(SearchRequest req, int coverCount) =>
        log.LogError(Template, "provider_failed", req.Intent, req.Query, coverCount, 0, "provider_failed");
}
```

- [ ] **Step 4: Implement `SearchPipeline`**

`src/Smx.SearchProxy/Pipeline/SearchPipeline.cs`:
```csharp
using Microsoft.Extensions.Logging;
using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Providers;

namespace Smx.SearchProxy.Pipeline;

public sealed record PipelineResult(SearchResponse? Response, int StatusCode, string? Reason);

/// The testable core. The trigger is a shell over this (house convention: SdsSweep.RunSweepAsync,
/// SyncPipeline.RunSyncAsync). `nowUtc` is a parameter so cache TTL and quota windows are deterministic.
///
/// Order matters and is not arbitrary:
///   guard  → nothing that should never egress can reach the quota, the cache key, or the provider
///   quota  → a runaway loop is stopped BEFORE the provider is called, not after the bill
///   cache  → a hit egresses nothing at all, which is the safest possible outcome
///   cover  → and only now, wrapped in decoys, does anything leave the building
public sealed class SearchPipeline(
    StructuralGuard guard,
    QuotaGuard quota,
    ISearchCache cache,
    CoverBatch cover,
    ISearchProvider provider,
    EgressAudit audit,
    ProxyOptions opts,
    ILogger<SearchPipeline> log)
{
    public async Task<PipelineResult> RunAsync(SearchRequest req, string nowUtc, CancellationToken ct)
    {
        var verdict = guard.Check(req);
        if (!verdict.Allowed)
        {
            audit.Blocked(req, verdict.Reason!);
            return new PipelineResult(null, 400, verdict.Reason);
        }

        if (!opts.DryRun && string.IsNullOrEmpty(opts.ApiKey))
        {
            audit.Blocked(req, "provider_not_configured");
            return new PipelineResult(null, 503, "provider_not_configured");
        }

        var key = CacheKey.For(req.Query, req.Intent, req.MaxResults);
        var cached = await cache.GetAsync(key, nowUtc, ct);
        if (cached is not null)
        {
            audit.CacheHit(req, cached.Count);
            return new PipelineResult(new SearchResponse(cached, cached.Count, CacheHit: true, CoverCount: 0), 200, null);
        }

        var batch = cover.Build(req.Query, req.Intent);

        // Charge the WHOLE batch, decoys included: that is what the provider bills and what actually egresses.
        if (!await quota.TryConsumeAsync(batch.Count, nowUtc, ct))
        {
            audit.Blocked(req, "quota_exceeded");
            return new PipelineResult(null, 429, "quota_exceeded");
        }

        // Concurrently — a serialized batch would leak an ordering signal, and the real query would sit at a
        // predictable position in time. Fired together, the N queries are indistinguishable by timing.
        var answers = await Task.WhenAll(batch.Select(async q =>
            (Query: q, Hits: await provider.SearchAsync(q, req.MaxResults, req.FreshnessDays, ct))));

        foreach (var (query, hits) in answers)
        {
            if (hits is null) continue;
            await cache.SetAsync(CacheKey.For(query, req.Intent, req.MaxResults), hits, nowUtc, ct);
        }

        var real = answers.First(a => a.Query == req.Query).Hits;
        if (real is null)
        {
            // NOT an empty result set. An agent reading [] would conclude "no such marker exists" and act on
            // it; a 502 tells it the question was never answered.
            log.LogWarning("Provider failed for the real query; {Decoys} decoys were issued regardless", batch.Count - 1);
            audit.ProviderFailed(req, batch.Count);
            return new PipelineResult(null, 502, "provider_failed");
        }

        audit.Allowed(req, batch.Count, real.Count);
        return new PipelineResult(new SearchResponse(real, real.Count, CacheHit: false, CoverCount: batch.Count), 200, null);
    }
}
```

- [ ] **Step 5: Run — it must pass**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter SearchPipelineTests
```
Expected: PASS, 9 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.SearchProxy/Pipeline src/Smx.SearchProxy.Tests/SearchPipelineTests.cs
git commit -m "feat(proxy): SearchPipeline — guard, quota, cache, cover, egress, audit

A provider failure returns 502, never an empty result set: an agent reading []
concludes 'no such marker exists' and acts on it. Absence of evidence is not
evidence of absence, and here the difference is a wrong marker recommendation."
```

---

## Task 10: The triggers + DI + the strict-binding invariant

**Files:**
- Create: `src/Smx.SearchProxy/Triggers/SearchHttp.cs`, `HealthHttp.cs`, `src/Smx.SearchProxy/Program.cs`
- Test: `src/Smx.SearchProxy.Tests/StrictBindingTests.cs`, `HostWiringTests.cs`

- [ ] **Step 1: Write the failing tests**

`src/Smx.SearchProxy.Tests/StrictBindingTests.cs`:
```csharp
using System.Text.Json;
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Triggers;

namespace Smx.SearchProxy.Tests;

public class StrictBindingTests
{
    // Invariant 3: PROJECT-BLIND. The contract has no field a project identifier could travel in, and the
    // deserializer REFUSES a body that carries one. A caller cannot smuggle context in "just this once".
    [Theory]
    [InlineData("""{"query":"yttrium forms","intent":"discovery.candidate_forms","projectId":"p-42"}""")]
    [InlineData("""{"query":"yttrium forms","intent":"discovery.candidate_forms","client":"Acme"}""")]
    [InlineData("""{"query":"yttrium forms","intent":"discovery.candidate_forms","url":"https://x.example"}""")]
    public void UnknownFields_AreRejected(string body)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<SearchRequest>(body, SearchHttp.StrictJson));
    }

    [Fact]
    public void TheContractItself_Binds()
    {
        var req = JsonSerializer.Deserialize<SearchRequest>(
            """{"query":"yttrium forms","intent":"discovery.candidate_forms","maxResults":5}""", SearchHttp.StrictJson);
        Assert.NotNull(req);
        Assert.Equal("yttrium forms", req!.Query);
        Assert.Equal(5, req.MaxResults);
    }
}
```

`src/Smx.SearchProxy.Tests/HostWiringTests.cs`:
```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Smx.SearchProxy;
using Smx.SearchProxy.Pipeline;
using Smx.SearchProxy.Providers;

namespace Smx.SearchProxy.Tests;

public class HostWiringTests
{
    private static IServiceProvider Build(params (string, string)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Item1, s => (string?)s.Item2))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        ProxyHost.ConfigureServices(services, config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void DryRun_BuildsWithNoKeyAndNoStorage()
    {
        var sp = Build(("PROXY_DRY_RUN", "true"));
        Assert.IsType<DryRunSearchProvider>(sp.GetRequiredService<ISearchProvider>());
        Assert.NotNull(sp.GetRequiredService<SearchPipeline>());
    }

    [Fact]
    public void Live_SelectsTheBraveProvider()
    {
        var sp = Build(
            ("PROXY_DRY_RUN", "false"),
            ("PROXY_SEARCH_API_KEY", "k"),
            ("AzureWebJobsStorage__accountName", "stfnspexample"));
        Assert.IsType<BraveSearchProvider>(sp.GetRequiredService<ISearchProvider>());
    }
}
```

- [ ] **Step 2: Run — they must fail**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj --filter "StrictBindingTests|HostWiringTests"
```
Expected: FAIL — `SearchHttp` / `ProxyHost` do not exist.

- [ ] **Step 3: Write the triggers**

`src/Smx.SearchProxy/Triggers/SearchHttp.cs`:
```csharp
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Pipeline;

namespace Smx.SearchProxy.Triggers;

/// Anonymous here; platform Easy Auth / Entra is enforced at the infra layer (functions.bicep
/// `searchProxyAuth`), and public inbound is disabled by harden.sh — this app is reached only over its
/// private endpoint, by the orchestrator, with an Entra token.
public sealed class SearchHttp(SearchPipeline pipeline, ILogger<SearchHttp> log)
{
    /// Invariant 3. UnmappedMemberHandling.Disallow is what makes "project-blind" enforceable rather than
    /// aspirational: a body carrying projectId / client / url is a 400, not a silently-ignored field.
    public static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [Function("Search")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "search")] HttpRequestData req)
    {
        var ct = req.FunctionContext.CancellationToken;

        SearchRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<SearchRequest>(req.Body, StrictJson, ct);
        }
        catch (JsonException ex)
        {
            log.LogWarning("SearchProxyAudit decision={Decision} reason={Reason} error={Error}",
                "blocked", "malformed_or_unknown_field", ex.Message);
            return await Error(req, HttpStatusCode.BadRequest,
                new SearchError("malformed_or_unknown_field",
                    "The request carried a field that is not part of the contract, or was not valid JSON. " +
                    "The Search Proxy is project-blind by design: it accepts only query, intent, maxResults, freshnessDays."));
        }
        if (body is null)
            return await Error(req, HttpStatusCode.BadRequest, new SearchError("empty_body", "A JSON body is required."));

        var result = await pipeline.RunAsync(body, DateTimeOffset.UtcNow.ToString("O"), ct);

        if (result.Response is not null)
        {
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(result.Response, ct);
            return ok;
        }
        return await Error(req, (HttpStatusCode)result.StatusCode,
            new SearchError(result.Reason ?? "error", Explain(result.Reason)));
    }

    /// The message is written for the MODEL, not for a human: it is relayed verbatim into the tool result, so
    /// it must tell the agent what to do differently.
    private static string Explain(string? reason) => reason switch
    {
        "query_empty" => "The query was empty. Ask a specific chemical question.",
        "query_too_long" => "The query was too long. Shorten it to a focused chemical question.",
        "unknown_intent" => "Unknown intent. Use discovery.candidate_forms, discovery.form_properties or discovery.supplier_availability.",
        "max_results_out_of_range" => "maxResults must be between 1 and 20.",
        "contains_guid" or "contains_email" or "contains_url" or "contains_digit_run" =>
            "The query contained an identifier (an id, an address, a URL or a long number). Rephrase it in generic chemical terms — " +
            "the external search must never carry anything that identifies this project.",
        "quota_exceeded" => "The external-search budget is exhausted. Continue from the catalog and the reference corpus.",
        "provider_failed" => "The external search did not answer. Do NOT treat this as 'no results exist' — it is not evidence of absence.",
        "provider_not_configured" => "External search is not configured. Continue from the catalog and the reference corpus.",
        _ => "The external search could not be completed.",
    };

    private static async Task<HttpResponseData> Error(HttpRequestData req, HttpStatusCode code, SearchError error)
    {
        var resp = req.CreateResponse(code);
        await resp.WriteAsJsonAsync(error, req.FunctionContext.CancellationToken);
        return resp;
    }
}
```

`src/Smx.SearchProxy/Triggers/HealthHttp.cs`:
```csharp
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.SearchProxy.Config;

namespace Smx.SearchProxy.Triggers;

public sealed class HealthHttp(ProxyOptions opts)
{
    /// Reports readiness WITHOUT leaking the key or any query. `configured` is what an operator actually
    /// needs to know: whether this proxy can currently answer.
    [Function("Health")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
    {
        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(new
        {
            status = "ok",
            provider = opts.Provider,
            dryRun = opts.DryRun,
            configured = opts.DryRun || !string.IsNullOrEmpty(opts.ApiKey),
            coverCount = opts.CoverCount,
        }, req.FunctionContext.CancellationToken);
        return resp;
    }
}
```

- [ ] **Step 4: Write `Program.cs`**

`src/Smx.SearchProxy/Program.cs`:
```csharp
using System.Diagnostics;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Smx.SearchProxy;
using Smx.SearchProxy.Anonymity;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Pipeline;
using Smx.SearchProxy.Providers;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) => ProxyHost.ConfigureServices(services, ctx.Configuration))
    .Build();

host.Run();

namespace Smx.SearchProxy
{
    /// Extracted from the top-level statements so HostWiringTests can build the real graph and catch a
    /// missing registration at test time rather than at 3am (the OrchestratorHostWiringTests pattern).
    public static class ProxyHost
    {
        public static void ConfigureServices(IServiceCollection services, IConfiguration config)
        {
            var opts = ProxyOptions.From(config);
            services.AddSingleton(opts);

            if (opts.CoverCountRaw != opts.CoverCount)
                services.AddSingleton<IStartupWarning>(sp => new StartupWarning(
                    sp.GetRequiredService<ILogger<StartupWarning>>(),
                    $"PROXY_COVER_COUNT={opts.CoverCountRaw} was clamped to {opts.CoverCount}: a real query may never egress alone."));

            services.AddSingleton(_ => CoverCorpus.FromFile(opts.CoverCorpusPath));
            services.AddSingleton<IShuffler, RandomShuffler>();
            services.AddSingleton<CoverBatch>();
            services.AddSingleton<StructuralGuard>();
            services.AddSingleton<EgressAudit>();

            if (opts.DryRun)
            {
                // No key, no storage, no network — the whole pipeline still runs.
                services.AddSingleton<ISearchProvider>(_ => new DryRunSearchProvider());
                services.AddSingleton<ISearchCache>(_ => new NullSearchCache());
                services.AddSingleton<IQuotaStore>(_ => new NullQuotaStore());
            }
            else
            {
                services.AddHttpClient<ISearchProvider, BraveSearchProvider>()
                    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                    {
                        // Suppress the W3C traceparent header .NET would otherwise inject into every outbound
                        // request. Sent to Brave it would be a free correlation handle — and worse, it would
                        // group the N queries of a cover batch under one trace id, telling the provider
                        // exactly which queries were issued together and defeating the k-anonymity outright.
                        ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
                    });

                TokenCredential cred = string.IsNullOrEmpty(opts.UamiClientId)
                    ? new DefaultAzureCredential()
                    : new ManagedIdentityCredential(opts.UamiClientId);
                services.AddSingleton(cred);

                var blobUri = new Uri($"https://{opts.StorageAccount}.blob.core.windows.net");
                services.AddSingleton(_ => new BlobServiceClient(blobUri, cred).GetBlobContainerClient(opts.CacheContainer));
                services.AddSingleton<ISearchCache, BlobSearchCache>();
                services.AddSingleton<IQuotaStore, BlobQuotaStore>();
            }

            services.AddSingleton<QuotaGuard>();
            services.AddSingleton<SearchPipeline>();
        }
    }

    public interface IStartupWarning;

    public sealed class StartupWarning : IStartupWarning
    {
        public StartupWarning(ILogger<StartupWarning> log, string message) => log.LogWarning("{Message}", message);
    }
}
```

Add the two dry-run stores next to the real ones — `src/Smx.SearchProxy/Pipeline/NullStores.cs`:
```csharp
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Pipeline;

/// Dry-run has no storage account to talk to. A cache that never hits and a quota that never binds keep the
/// pipeline's shape identical to production — the dry run exercises the real code path, not a shortcut.
public sealed class NullSearchCache : ISearchCache
{
    public Task<IReadOnlyList<SearchHit>?> GetAsync(string key, string nowUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SearchHit>?>(null);
    public Task SetAsync(string key, IReadOnlyList<SearchHit> hits, string nowUtc, CancellationToken ct) =>
        Task.CompletedTask;
}

public sealed class NullQuotaStore : IQuotaStore
{
    public Task<int> ReadAsync(string month, CancellationToken ct) => Task.FromResult(0);
    public Task<int> AddAsync(string month, int delta, CancellationToken ct) => Task.FromResult(delta);
}
```

- [ ] **Step 5: Run the whole proxy suite**

```bash
dotnet test src/Smx.SearchProxy.Tests/Smx.SearchProxy.Tests.csproj
```
Expected: PASS, all tests (roughly 40).

- [ ] **Step 6: Commit**

```bash
git add src/Smx.SearchProxy src/Smx.SearchProxy.Tests
git commit -m "feat(proxy): HTTP triggers + DI — strict binding makes project-blindness enforceable

UnmappedMemberHandling.Disallow turns 'we don't send a projectId' into 'a body
carrying one is a 400'. And the outbound handler suppresses the W3C traceparent
header: sent to Brave it would group a cover batch under one trace id and defeat
the k-anonymity outright."
```

---

## Task 11: CasNumber — the check-digit rail

A CAS number read off a search snippet is the likeliest hallucination in this whole design, and a wrong CAS silently corrupts the regulatory screen, the dosing maths and procurement. CAS numbers carry a check digit; validating it is deterministic and cheap.

This rail already earned its keep: it is what found `15492-49-8` in the seeded catalog (Task 0).

**Files:**
- Create: `src/Smx.Domain/CasNumber.cs`
- Test: `src/Smx.Domain.Tests/CasNumberTests.cs`

- [ ] **Step 1: Write the failing test**

`src/Smx.Domain.Tests/CasNumberTests.cs`:
```csharp
using Smx.Domain;

namespace Smx.Domain.Tests;

public class CasNumberTests
{
    // Real CAS numbers from the seeded catalog.
    [Theory]
    [InlineData("1314-36-9")]   // yttrium oxide
    [InlineData("80326-98-3")]  // yttrium 2-ethylhexanoate
    [InlineData("7732-18-5")]   // water — the textbook example
    [InlineData("15492-49-6")]  // Sc(TMHD)3, anhydrous — the CORRECTED value from Task 0
    public void ValidCas_IsAccepted(string cas) => Assert.True(CasNumber.IsValid(cas));

    // The check digit is the whole point: a single transposed digit must not pass.
    [Theory]
    [InlineData("15492-49-8")]  // the typo Task 0 removed from the catalog
    [InlineData("1314-36-8")]
    [InlineData("7732-18-4")]
    public void WrongCheckDigit_IsRejected(string cas) => Assert.False(CasNumber.IsValid(cas));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-cas")]
    [InlineData("1314-36")]        // missing the check digit
    [InlineData("1314-3-69")]      // middle group must be exactly 2 digits
    [InlineData("1-36-9")]         // first group must be 2..7 digits
    [InlineData("12345678-36-9")]  // first group too long
    [InlineData("1314-36-99")]     // check digit must be a single digit
    public void Malformed_IsRejected(string? cas) => Assert.False(CasNumber.IsValid(cas));
}
```

- [ ] **Step 2: Run — it must fail**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter CasNumberTests
```
Expected: FAIL — `CasNumber` does not exist.

- [ ] **Step 3: Implement**

`src/Smx.Domain/CasNumber.cs`:
```csharp
namespace Smx.Domain;

/// A CAS Registry Number carries a check digit, and that makes it self-validating — the one identifier in
/// this system that can be proved wrong without consulting anything.
///
/// Why we care: an agent that reads a CAS off a web snippet can transpose a digit, and nothing downstream
/// would notice. The regulatory screen would clear the WRONG substance, dosing would compute against the
/// wrong molecular weight, and procurement would order it. This is the cheapest possible guard against the
/// system's headline harm, and it applies to catalog candidates too — it is how the invalid Sc(TMHD)3 entry
/// (15492-49-8, check digit should be 6) was found in the seeded catalog.
///
/// Format: 2-7 digits, hyphen, 2 digits, hyphen, 1 check digit.
/// Check: sum of each digit multiplied by its position from the right (1-based), mod 10.
///   1314-36-9 → 6*1 + 3*2 + 4*3 + 1*4 + 3*5 + 1*6 = 49 → 49 mod 10 = 9 ✓
public static class CasNumber
{
    public static bool IsValid(string? cas)
    {
        if (string.IsNullOrWhiteSpace(cas)) return false;

        var parts = cas.Trim().Split('-');
        if (parts.Length != 3) return false;
        if (parts[0].Length is < 2 or > 7 || parts[1].Length != 2 || parts[2].Length != 1) return false;
        if (!parts.All(p => p.All(char.IsAsciiDigit))) return false;

        var digits = parts[0] + parts[1];
        var checkDigit = parts[2][0] - '0';

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
            sum += (digits.Length - i) * (digits[i] - '0');

        return sum % 10 == checkDigit;
    }
}
```

- [ ] **Step 4: Run — it must pass**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter CasNumberTests
```
Expected: PASS, 16 cases.

- [ ] **Step 5: Prove the whole seeded catalog is now clean**

The seed files live in `Smx.Functions`, but `CasNumber` lives in `Smx.Domain` and `Smx.Functions` does not reference `Smx.Domain` (nor should it — the Functions app and the backend are deliberately separate stacks). So put the test in `Smx.Domain.Tests` and have it read the seed files from disk by relative path. No project reference, no duplicated validator.

`src/Smx.Domain.Tests/SeedCasIntegrityTests.cs`:
```csharp
using System.Text.RegularExpressions;
using Smx.Domain;

namespace Smx.Domain.Tests;

/// The seeded catalog is what Discovery proposes candidates FROM. A bad CAS in it is a bad CAS in a
/// recommendation. This test is the regression guard for the 15492-49-8 defect: it fails the build if an
/// invalid CAS is ever reintroduced by a reference-data regeneration.
public class SeedCasIntegrityTests
{
    [Fact]
    public void EverySeededCasNumberPassesItsCheckDigit()
    {
        var seedDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Smx.Functions", "Reference", "Seed");
        Assert.True(Directory.Exists(seedDir), $"seed directory not found: {Path.GetFullPath(seedDir)}");

        var invalid = new List<string>();
        foreach (var file in new[] { "catalog-products.json", "catalog-elements.json" })
        {
            var text = File.ReadAllText(Path.Combine(seedDir, file));
            foreach (Match m in Regex.Matches(text, @"\b\d{2,7}-\d{2}-\d\b"))
                if (!CasNumber.IsValid(m.Value))
                    invalid.Add($"{file}: {m.Value}");
        }

        Assert.True(invalid.Count == 0,
            "Invalid CAS numbers in the seeded catalog (check digit failed):\n  " + string.Join("\n  ", invalid.Distinct()));
    }
}
```

- [ ] **Step 6: Run it — it must pass now that Task 0 fixed the data**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter SeedCasIntegrityTests
```
Expected: PASS. (If it fails, Task 0 was not applied — go back and apply it.)

- [ ] **Step 7: Commit**

```bash
git add src/Smx.Domain/CasNumber.cs src/Smx.Domain.Tests/CasNumberTests.cs src/Smx.Domain.Tests/SeedCasIntegrityTests.cs
git commit -m "feat(domain): CAS check-digit validation + a seed-integrity guard

A CAS read off a search snippet is the likeliest hallucination in the web-search
design, and a wrong one silently clears the wrong substance through the
regulatory gate. The check digit makes it provable. The seed guard is the
regression test for the 15492-49-8 typo this validator found."
```

---

## Task 12: The web-search tool client (orchestrator side)

Three pieces, one file each: the Entra-authed HTTP client, the per-project term guard, and the tool that puts them together behind `IWebSearch`.

**Files:**
- Modify: `src/Smx.Domain/Tools/ITools.cs`
- Modify: `src/Smx.Infrastructure/Smx.Infrastructure.csproj` (reference the contracts project)
- Create: `src/Smx.Infrastructure/Search/SensitiveTermGuard.cs`, `SearchProxyClient.cs`, `WebSearchTool.cs`
- Test: `src/Smx.Orchestrator.Tests/WebSearchToolTests.cs`

- [ ] **Step 1: Extend the tool contracts**

Append to `src/Smx.Domain/Tools/ITools.cs`:
```csharp
/// One web result, as the agent sees it. Deliberately NOT a RetrievedChunk: a web hit is not a retrieved
/// corpus chunk, it has no index reference, and the difference must survive all the way to the citation.
public sealed record WebHit(string Title, string Url, string Snippet, string Host);

/// Anonymized external search, via the Search Proxy. The ONLY tool in this system that reaches the public
/// internet at agent time, and it is exposed to Discovery ALONE — never to Regulatory, whose verdicts may
/// rest only on the curated, sync-dated, R.E.-gated corpus (spec §2 D4).
///
/// A failure is not an empty list. `WebSearchResult.Note` carries the reason (blocked, quota, provider
/// down) so the agent can tell "I searched and found nothing" from "I never got an answer" — the second is
/// not evidence of absence, and an agent that confuses them will confidently exclude a good marker.
public sealed record WebSearchResult(IReadOnlyList<WebHit> Hits, string? Note);

public interface IWebSearch
{
    Task<WebSearchResult> SearchAsync(string query, string intent, CancellationToken ct = default);
}

/// The client/product/project names of the project currently being worked on. This type exists so the terms
/// are passed EXPLICITLY into the tool that must reject them: a tool constructed without them would be a
/// tool that cannot protect the project, and the compiler now says so.
public sealed record SensitiveTerms(IReadOnlyList<string> Terms)
{
    public static SensitiveTerms None => new([]);
}
```

- [ ] **Step 2: Write the failing tests**

`src/Smx.Orchestrator.Tests/WebSearchToolTests.cs`:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain.Tools;
using Smx.Infrastructure.Search;

namespace Smx.Orchestrator.Tests;

public class SensitiveTermGuardTests
{
    private static readonly SensitiveTerms Terms = new(["Acme Bottling", "HydroFizz", "proj-2026-014"]);

    [Theory]
    [InlineData("Acme Bottling marker candidates")]
    [InlineData("markers for the HYDROFIZZ bottle")]        // case-insensitive
    [InlineData("proj-2026-014 discovery")]
    public void QueriesCarryingAProjectIdentifier_AreRejected(string query)
    {
        Assert.False(SensitiveTermGuard.IsClean(query, Terms, out var offender));
        Assert.NotNull(offender);
    }

    // The whole point of a taggant search is chemistry. Rejecting ordinary chemical language would make the
    // tool useless — and a guard that fires constantly gets turned off.
    [Theory]
    [InlineData("ytterbium neodecanoate solubility in polyethylene")]
    [InlineData("rare earth taggant forms for PET bottles")]
    public void OrdinaryChemistryQueries_AreClean(string query) =>
        Assert.True(SensitiveTermGuard.IsClean(query, Terms, out _));

    // Token-boundary aware: a client called "Ion" must not blacklist the word "ionic".
    [Fact]
    public void MatchesWholeTokensOnly()
    {
        var terms = new SensitiveTerms(["Ion"]);
        Assert.True(SensitiveTermGuard.IsClean("ionic solubility of yttrium", terms, out _));
        Assert.False(SensitiveTermGuard.IsClean("markers for Ion beverages", terms, out _));
    }
}

public class WebSearchToolTests
{
    private sealed class FakeProxy(WebSearchResult result) : ISearchProxyClient
    {
        public readonly List<string> Sent = [];
        public Task<WebSearchResult> SearchAsync(string query, string intent, int maxResults, CancellationToken ct)
        {
            Sent.Add(query);
            return Task.FromResult(result);
        }
    }

    private static readonly WebSearchResult Anything =
        new([new WebHit("t", "https://example.org/a", "s", "example.org")], null);

    private static WebSearchTool Tool(FakeProxy proxy, SensitiveTerms terms, bool enabled = true, int budget = 8) =>
        new(proxy, terms, enabled, budget, NullLogger<WebSearchTool>.Instance);

    [Fact]
    public async Task CleanQuery_ReachesTheProxy()
    {
        var proxy = new FakeProxy(Anything);
        var result = await Tool(proxy, new SensitiveTerms(["Acme"])).SearchAsync("yttrium forms", "discovery.candidate_forms");

        Assert.Single(proxy.Sent);
        Assert.Single(result.Hits);
    }

    // Reject, do not strip. A silently-mangled query returns garbage the agent then cites; a rejection tells
    // the agent what it did wrong, and lands in the audit log where the operator can see it.
    [Fact]
    public async Task QueryWithAProjectIdentifier_NeverLeavesTheVnet()
    {
        var proxy = new FakeProxy(Anything);
        var result = await Tool(proxy, new SensitiveTerms(["Acme"])).SearchAsync("Acme marker forms", "discovery.candidate_forms");

        Assert.Empty(proxy.Sent);
        Assert.Empty(result.Hits);
        Assert.Contains("identifies this project", result.Note);
    }

    [Fact]
    public async Task KillSwitchOff_NeverCallsTheProxy()
    {
        var proxy = new FakeProxy(Anything);
        var result = await Tool(proxy, SensitiveTerms.None, enabled: false).SearchAsync("yttrium forms", "discovery.candidate_forms");

        Assert.Empty(proxy.Sent);
        Assert.Contains("disabled", result.Note);
    }

    // An agent loop must not be able to spray egress.
    [Fact]
    public async Task StageBudget_IsEnforced()
    {
        var proxy = new FakeProxy(Anything);
        var tool = Tool(proxy, SensitiveTerms.None, budget: 2);

        await tool.SearchAsync("q1", "discovery.candidate_forms");
        await tool.SearchAsync("q2", "discovery.candidate_forms");
        var third = await tool.SearchAsync("q3", "discovery.candidate_forms");

        Assert.Equal(2, proxy.Sent.Count);
        Assert.Contains("budget", third.Note);
    }
}
```

- [ ] **Step 3: Run — it must fail**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter "SensitiveTermGuardTests|WebSearchToolTests"
```
Expected: FAIL — the types do not exist.

- [ ] **Step 4: Reference the contracts project**

In `src/Smx.Infrastructure/Smx.Infrastructure.csproj`, add to the existing `ProjectReference` group:
```xml
    <ProjectReference Include="..\Smx.SearchProxy.Contracts\Smx.SearchProxy.Contracts.csproj" />
```
And add the project to the backend solution:
```bash
dotnet sln src/Smx.Backend.sln add src/Smx.SearchProxy.Contracts/Smx.SearchProxy.Contracts.csproj
```

- [ ] **Step 5: Implement `SensitiveTermGuard`**

`src/Smx.Infrastructure/Search/SensitiveTermGuard.cs`:
```csharp
using System.Text.RegularExpressions;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// Layer 1 of the anonymization (spec §6.1), and the only layer that can possibly do this job.
///
/// The Search Proxy is project-blind: it cannot know that "Acme Bottling" is a client name, and giving it
/// the client roster would put that list in git on the internet-facing component. The orchestrator already
/// holds the names (ProjectDoc.Client, ProjectDoc.Product, ProjectId) — so the identity check belongs here,
/// and the structural check belongs there.
public static class SensitiveTermGuard
{
    public static bool IsClean(string query, SensitiveTerms terms, out string? offendingTerm)
    {
        foreach (var term in terms.Terms)
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            // Whole-token match: a client called "Ion" must not blacklist "ionic".
            var pattern = $@"(?<![\w-]){Regex.Escape(term.Trim())}(?![\w-])";
            if (Regex.IsMatch(query, pattern, RegexOptions.IgnoreCase))
            {
                offendingTerm = term;
                return false;
            }
        }
        offendingTerm = null;
        return true;
    }
}
```

- [ ] **Step 6: Implement `SearchProxyClient`**

`src/Smx.Infrastructure/Search/SearchProxyClient.cs`:
```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Smx.Domain.Tools;
using Smx.SearchProxy.Contracts;

namespace Smx.Infrastructure.Search;

public interface ISearchProxyClient
{
    Task<WebSearchResult> SearchAsync(string query, string intent, int maxResults, CancellationToken ct);
}

/// Talks to the Search Proxy over its private endpoint, with an Entra token for the proxy's Easy Auth
/// audience. Note what is NOT sent: no project id, no correlation id, no client name — the request record
/// has no field for them, so this client could not send them if it tried.
public sealed class SearchProxyClient(
    HttpClient http, TokenCredential credential, string endpoint, string audience, ILogger<SearchProxyClient> log)
    : ISearchProxyClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WebSearchResult> SearchAsync(string query, string intent, int maxResults, CancellationToken ct)
    {
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([$"{audience}/.default"]), ct);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.TrimEnd('/')}/api/search")
            {
                Content = JsonContent.Create(new SearchRequest(query, intent, maxResults), options: Json),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

            using var resp = await http.SendAsync(req, ct);

            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(Json, ct);
                if (body is null) return new WebSearchResult([], "the search proxy returned an empty body");
                var hits = body.Results.Select(r => new WebHit(r.Title, r.Url, r.Snippet, r.Host)).ToList();
                return new WebSearchResult(hits, null);
            }

            // The proxy's error message is written FOR the model (SearchHttp.Explain) — relay it verbatim so
            // the agent learns what to do differently instead of just seeing nothing come back.
            var error = await resp.Content.ReadFromJsonAsync<SearchError>(Json, ct);
            log.LogWarning("Search proxy → {Status} {Reason}", (int)resp.StatusCode, error?.Reason);
            return new WebSearchResult([], error?.Message ?? $"the search proxy returned {(int)resp.StatusCode}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Search proxy call failed");
            return new WebSearchResult([], "the external search is unavailable — do NOT treat this as 'no results exist'");
        }
    }
}
```

- [ ] **Step 7: Implement `WebSearchTool`**

`src/Smx.Infrastructure/Search/WebSearchTool.cs`:
```csharp
using Microsoft.Extensions.Logging;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// IWebSearch, with the three controls that must sit INSIDE the VNet, before anything can egress:
///   1. the operator kill switch (WEB_SEARCH_ENABLED),
///   2. the per-project sensitive-term guard — the only layer that knows the client/product names,
///   3. a per-stage query budget, so an agent loop cannot spray egress or burn the provider quota.
///
/// Constructed per stage run (not a singleton) because SensitiveTerms and the budget counter are per project.
public sealed class WebSearchTool(
    ISearchProxyClient proxy,
    SensitiveTerms terms,
    bool enabled,
    int maxQueriesPerStage,
    ILogger<WebSearchTool> log) : IWebSearch
{
    private int _used;

    public async Task<WebSearchResult> SearchAsync(string query, string intent, CancellationToken ct = default)
    {
        if (!enabled)
            return new WebSearchResult([], "external web search is disabled — answer from the catalog and the reference corpus");

        if (!SensitiveTermGuard.IsClean(query, terms, out var offender))
        {
            // The offending term is logged, not returned: the model does not need to be told the client's
            // name to be told it must not use it.
            log.LogWarning("Web search blocked: the query contained the project term {Term}", offender);
            return new WebSearchResult([],
                "that query contained a term that identifies this project (a client, product or project name). " +
                "Rephrase it in generic chemical terms — the external search must never carry anything that identifies this project.");
        }

        if (Interlocked.Increment(ref _used) > maxQueriesPerStage)
            return new WebSearchResult([],
                $"the external-search budget for this stage ({maxQueriesPerStage} queries) is spent — " +
                "continue from the catalog and the reference corpus");

        return await proxy.SearchAsync(query, intent, maxResults: 10, ct);
    }
}
```

- [ ] **Step 8: Run — it must pass**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter "SensitiveTermGuardTests|WebSearchToolTests"
```
Expected: PASS, 10 tests.

- [ ] **Step 9: Commit**

```bash
git add src/Smx.Domain/Tools/ITools.cs src/Smx.Infrastructure src/Smx.Orchestrator.Tests/WebSearchToolTests.cs src/Smx.Backend.sln
git commit -m "feat(search): IWebSearch — kill switch, per-project term guard, per-stage budget

The guard rejects rather than strips: a silently-mangled query returns garbage
the agent then cites, while a rejection teaches the model and lands in the audit
log. It is also the only layer that CAN do this — the proxy is project-blind and
must never hold the client roster."
```

---

## Task 13: ToolBox — expose `search_web` to Discovery, and to Discovery alone

**Files:**
- Modify: `src/Smx.Orchestrator/Agents/ToolBox.cs`
- Modify: `src/Smx.Orchestrator.Tests/ToolBoxTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `src/Smx.Orchestrator.Tests/ToolBoxTests.cs`. First update the existing `Box(...)` helper to supply the new dependency, then add the tests:

```csharp
    // Replace the existing Box(...) helper with this one — ToolBox now takes an IWebSearch factory.
    private static ToolBox Box(
        Smx.Domain.IKnowledgeStore? knowledge = null,
        Smx.Domain.Tools.ILearnedConclusionsSearch? learnedConclusions = null,
        Smx.Domain.Tools.IWebSearch? web = null)
    {
        var search = new FakeSearch();
        return new ToolBox(
            new FakeCatalogLookup(), new FakeCompatibilityLookup(), search, search, search,
            knowledge ?? new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore(),
            learnedConclusions ?? new FakeLearnedConclusionsSearch(),
            _ => web ?? new FakeWebSearch());
    }

    [Fact]
    public void DiscoveryTools_IncludeSearchWeb()
    {
        var names = Box().DiscoveryTools(Smx.Domain.Tools.SensitiveTerms.None).Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Equal(
            ["lookup_compatibility", "search_catalog", "search_learned_conclusions", "search_reference", "search_web"],
            names);
    }

    // THE INVARIANT (spec §3, #5). A regulatory verdict may rest ONLY on the curated, sync-dated,
    // R.E.-gated corpus. The web is not a source of law. Tool-list membership is the enforcement — MAF can
    // only call a tool it was handed — so this assertion IS the control, not a description of it.
    [Fact]
    public void RegulatoryTools_NeverIncludeSearchWeb()
    {
        var names = Box().RegulatoryTools().Select(t => t.Name).ToArray();
        Assert.DoesNotContain("search_web", names);
    }

    [Fact]
    public void IntakeTools_NeverIncludeSearchWeb()
    {
        var names = Box().IntakeTools().Select(t => t.Name).ToArray();
        Assert.DoesNotContain("search_web", names);
    }

    // A failure must not read as "nothing exists" — that is how an agent confidently excludes a good marker.
    [Fact]
    public async Task SearchWeb_RelaysTheNote_WhenTheProxyRefuses()
    {
        var web = new FakeWebSearch { Result = new Smx.Domain.Tools.WebSearchResult([], "the external search is unavailable") };
        var json = await Box(web: web).SearchWebAsync("yttrium forms", "discovery.candidate_forms", default);
        Assert.Contains("unavailable", json);
        Assert.DoesNotContain("no matches", json);
    }

    [Fact]
    public async Task SearchWeb_EmptyResults_SaysSoWithoutInventing()
    {
        var web = new FakeWebSearch { Result = new Smx.Domain.Tools.WebSearchResult([], null) };
        var json = await Box(web: web).SearchWebAsync("yttrium forms", "discovery.candidate_forms", default);
        Assert.Contains("no matches", json);
    }

    // Web hits must be machine-identifiable as web-derived all the way to the citation, so the Tier-A rail
    // (Task 15) can find them. The source is "web:<host>", never a corpus name.
    [Fact]
    public async Task SearchWeb_TagsEveryHitWithAWebSource()
    {
        var web = new FakeWebSearch
        {
            Result = new Smx.Domain.Tools.WebSearchResult(
                [new Smx.Domain.Tools.WebHit("Yttrium 2-EH", "https://pubchem.ncbi.nlm.nih.gov/compound/1", "CAS 80326-98-3", "pubchem.ncbi.nlm.nih.gov")],
                null),
        };
        var json = await Box(web: web).SearchWebAsync("yttrium forms", "discovery.candidate_forms", default);
        Assert.Contains("web:pubchem.ncbi.nlm.nih.gov", json);
        Assert.Contains("https://pubchem.ncbi.nlm.nih.gov/compound/1", json);
    }
```

`src/Smx.Orchestrator.Tests/Fakes/FakeWebSearch.cs`:
```csharp
using Smx.Domain.Tools;

namespace Smx.Orchestrator.Tests.Fakes;

public sealed class FakeWebSearch : IWebSearch
{
    public readonly List<string> Queries = [];
    public WebSearchResult Result { get; set; } = new([], null);

    public Task<WebSearchResult> SearchAsync(string query, string intent, CancellationToken ct = default)
    {
        Queries.Add(query);
        return Task.FromResult(Result);
    }
}
```

- [ ] **Step 2: Run — it must fail**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter ToolBoxTests
```
Expected: FAIL — `ToolBox` has no `SearchWebAsync` and `DiscoveryTools` takes no argument.

- [ ] **Step 3: Modify `ToolBox`**

In `src/Smx.Orchestrator/Agents/ToolBox.cs`:

Change the primary constructor to take a **factory** — `IWebSearch` is per-project (it holds the sensitive terms and the stage budget), so it cannot be a singleton like the others. Append it as the **last** parameter; the existing order is unchanged:
```csharp
public sealed class ToolBox(
    ICatalogLookup catalog,
    ICompatibilityLookup compatibility,
    IRegulatorySearch regulatory,
    ISdsSearch sds,
    IReferenceSearch reference,
    IKnowledgeStore knowledge,
    ILearnedConclusionsSearch learnedConclusions,
    Func<SensitiveTerms, IWebSearch> webSearchFactory)
```
Add `using Smx.Domain.Tools;` if it is not already imported (it is — `RetrievedChunk` comes from there).

Change `DiscoveryTools()` to take the project's terms and to build a per-run `IWebSearch`:
```csharp
    /// SensitiveTerms is a REQUIRED parameter, not an optional one: a Discovery tool set built without the
    /// project's client/product names is a tool set that cannot protect the project. Forgetting it is now a
    /// compile error — the same reasoning the codebase applies to RevisionDoc? in the agent runners.
    public IList<AITool> DiscoveryTools(SensitiveTerms terms)
    {
        var web = webSearchFactory(terms);
        return
        [
            AIFunctionFactory.Create(SearchCatalogAsync, "search_catalog",
                "List the catalog products (form, molecule, CAS, purity, supplier) available for an element from the SMX catalog. Call this FIRST — it is the authoritative source for a candidate's CAS."),
            AIFunctionFactory.Create(LookupCompatibilityAsync, "lookup_compatibility",
                "Exact tabulated element×substrate compatibility verdict. Use as a tiering signal — an incompatible substrate lowers a candidate's tier or excludes it."),
            AIFunctionFactory.Create(SearchReferenceAsync, "search_reference",
                "Search SMX reference prose: solubility, XRF cleanliness, marker forms, bibliography-backed notes. Use to justify form ranking and tiering."),
            AIFunctionFactory.Create(SearchLearnedConclusionsAsync, "search_learned_conclusions",
                "Search accumulated Learned Conclusions (prior material/regulatory findings with confidence + provenance) relevant to tiering this element/form. Treat them as prior evidence, not fact; a higher-confidence, more recent conclusion supersedes an older one."),
            AIFunctionFactory.Create(
                (string query, string intent, CancellationToken ct) => SearchWebAsync(query, intent, ct, web),
                "search_web",
                "Anonymized external web search, for candidate forms the SMX catalog does not carry. It is a STARTING POINT, not an authority: a web hit may suggest a marker, it can never endorse one. " +
                "Corroborate every web finding against search_catalog and search_reference before you rely on it, and NEVER state a CAS you did not read from a retrieved source. " +
                "A candidate supported only by web citations must be Tier B with its limitation named in the rationale — it can never be Tier A or preferred. " +
                "The query must contain NO client, product or project name — only chemistry. " +
                "intent must be one of: discovery.candidate_forms, discovery.form_properties, discovery.supplier_availability."),
        ];
    }
```

Add the tool method (near the other `Search*Async` methods):
```csharp
    /// A web hit is NOT a RetrievedChunk. Its source is "web:<host>" so that a citation built from it stays
    /// machine-identifiable as web-derived all the way into the candidates doc — which is what lets
    /// DiscoveryAgent.Validate enforce the Tier-A rail deterministically instead of trusting the prompt.
    public async Task<string> SearchWebAsync(string query, string intent, CancellationToken ct) =>
        await SearchWebAsync(query, intent, ct, webSearchFactory(SensitiveTerms.None));

    private static async Task<string> SearchWebAsync(string query, string intent, CancellationToken ct, IWebSearch web)
    {
        var result = await web.SearchAsync(query, intent, ct);

        // A refusal/failure is NOT "no matches". Relay the note so the agent can tell "I searched and found
        // nothing" from "I never got an answer" — treating the second as the first is how a good marker gets
        // confidently excluded.
        if (result.Note is not null)
            return JsonSerializer.Serialize(new { results = Array.Empty<object>(), note = result.Note }, Json.Options);

        if (result.Hits.Count == 0)
            return "{\"results\":[],\"note\":\"no matches — do not invent facts; stay with the catalog candidates\"}";

        return JsonSerializer.Serialize(new
        {
            results = result.Hits.Select(h => new AgentVisibleChunk($"web:{h.Host}", h.Url, $"{h.Title} — {h.Snippet}")),
            note = "WEB SOURCE: a starting point, not an authority. Corroborate against search_catalog before relying on it. " +
                   "A candidate whose citations are all web sources must be Tier B, never Tier A and never preferred.",
        }, Json.Options);
    }
```

> The `SearchWebAsync(query, intent, ct)` public overload exists only so the tests can drive the method directly, matching how `ToolBoxTests` already drives `SearchCatalogAsync` etc. The tool the model actually calls is the closure over the per-project `IWebSearch`.

- [ ] **Step 4: Run — it must pass**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter ToolBoxTests
```
Expected: PASS — including the pre-existing ToolBox tests, which you must update for the new `Box(...)` helper and the `DiscoveryTools(SensitiveTerms)` signature.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Agents/ToolBox.cs src/Smx.Orchestrator.Tests
git commit -m "feat(tools): search_web for Discovery — and a test that Regulatory can never have it

MAF can only call a tool it was handed, so tool-list membership IS the control.
RegulatoryTools_NeverIncludeSearchWeb is not a description of the invariant, it
is the enforcement of it."
```

---

## Task 14: Plumb the ProjectDoc to Discovery

`RunDiscoveryAsync` currently receives only a `ConstraintsDoc`, which carries no client or product name — so the tool cannot be given the terms it must reject. The `ProjectDoc` has them (`Client`, `Product`, `ProjectId`).

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/AgentRuns.cs`
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs:66,199`
- Modify: any test/fake implementing `IAgentRuns`

- [ ] **Step 1: Change the interface**

In `src/Smx.Orchestrator/Dispatch/AgentRuns.cs`:
```csharp
    /// <param name="project">carries Client / Product / ProjectId — the terms the web-search tool must
    /// refuse to send. Required, not optional: a Discovery run without them is a run that cannot protect the
    /// project, and that must be a compile error rather than a silent leak.</param>
    /// <param name="revision">null for an ordinary run; non-null re-runs the stage applying the operator's
    /// revise-with-reason.</param>
    Task<AgentRunResult<CandidatesDoc>> RunDiscoveryAsync(
        ProjectDoc project, ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct);
```

And the implementation:
```csharp
    public Task<AgentRunResult<CandidatesDoc>> RunDiscoveryAsync(
        ProjectDoc project, ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct) =>
        DiscoveryAgent.RunAsync(
            new MafAgent(chatClient, DiscoveryAgent.AgentName, DiscoveryAgent.Instructions,
                toolBox.DiscoveryTools(TermsFor(project))),
            constraints, revision, ct);

    /// The project's own identifiers. These are exactly the strings that must never reach an external search:
    /// each one, in an outbound query, tells the provider which client is evaluating which chemistry.
    private static SensitiveTerms TermsFor(ProjectDoc p) =>
        new([p.Client, p.Product, p.ProjectId]
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList());
```

- [ ] **Step 2: Update the two call sites in `StageDispatcher`**

Both `StageDispatcher.cs:66` and `:199` call `agents.RunDiscoveryAsync(c, …)`. The dispatcher already reads the `ProjectDoc` to mutate stage state (see the `mutate(p.Stages[stage])` helper around line 322) — reuse that same read. Load the project by `c.ProjectId` before the call and pass it:

```csharp
// line ~66 (ordinary run)
var project = await LoadProjectAsync(c.ProjectId, ct);
var result = await agents.RunDiscoveryAsync(project, c, null, ct);

// line ~199 (revision re-run)
var project = await LoadProjectAsync(c.ProjectId, ct);
var result = await agents.RunDiscoveryAsync(project, c, r, ct);
```

Read `StageDispatcher.cs` and reuse whatever it already uses to fetch the `ProjectDoc` for the stage-state update (a record-store read by id, same partition — it is already in the file). If no such helper exists as a callable method, extract one:
```csharp
    private async Task<ProjectDoc> LoadProjectAsync(string projectId, CancellationToken ct) =>
        await store.ReadAsync<ProjectDoc>(RecordIds.Project(projectId), projectId, ct)
        ?? throw new InvalidOperationException($"project {projectId} not found");
```
> Match the actual record-store API in the file; do not invent a new one.

- [ ] **Step 3: Fix every other caller and fake**

```bash
dotnet build src/Smx.Backend.sln 2>&1 | grep -E "error|Error" | head -20
```
Fix each compile error by threading the `ProjectDoc` through. Expect hits in `Smx.Orchestrator.Tests` fakes and in any `StageDispatcher` test.

- [ ] **Step 4: Build + full test run**

```bash
dotnet build src/Smx.Backend.sln && dotnet test src/Smx.Backend.sln
```
Expected: `Build succeeded`, all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator
git commit -m "refactor(dispatch): hand Discovery its ProjectDoc — the terms the web must never see

The client and product names live on ProjectDoc; the constraints doc has neither.
Making the parameter required rather than optional means a Discovery run that
cannot protect the project is a compile error, not a silent leak."
```

---

## Task 15: The Discovery correctness rails

Two deterministic rails in `Validate`, where the error string is fed back to the agent and it retries. Prompt text is guidance; this is enforcement.

**Files:**
- Modify: `src/Smx.Orchestrator/Agents/DiscoveryAgent.cs`
- Test: `src/Smx.Orchestrator.Tests/DiscoveryAgentTests.cs` (add to the existing file)

- [ ] **Step 1: Write the failing tests**

Add to `src/Smx.Orchestrator.Tests/DiscoveryAgentTests.cs`:
```csharp
    private static ConstraintsDoc Constraints() => new()
    {
        Id = "constraints|p1", ProjectId = "p1",
        Components = [new ComponentSpec("bottle", "HDPE", "beverage", ["EU"], "covert")],
        ElementPools = [new ElementPool("bottle", "Y", "Ka", "V")],
    };

    private static CandidateSubstance Candidate(string tier, bool preferred, string cas, params Citation[] citations) =>
        new("bottle", "Y", "2-ethylhexanoate", cas, null, null, preferred, tier, "because", citations);

    private static readonly Citation WebCite = new("web:pubchem.ncbi.nlm.nih.gov", "https://pubchem.ncbi.nlm.nih.gov/compound/1", "2026-07-13T10:00:00Z");
    private static readonly Citation CatalogCite = new("catalog", "ref-catalog/product|Y|y-2eh", "2026-07-13T10:00:00Z");

    // RAIL 1. The web can SUGGEST a marker; only the catalog and the reference corpus can ENDORSE one.
    // Tier A is an endorsement.
    [Fact]
    public void WebOnlyCitations_CannotBeTierA()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("A", false, "80326-98-3", WebCite)] };
        var error = DiscoveryAgent.Validate(output, Constraints());
        Assert.NotNull(error);
        Assert.Contains("Tier A", error);
    }

    [Fact]
    public void WebOnlyCitations_CannotBePreferred()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("B", true, "80326-98-3", WebCite)] };
        var error = DiscoveryAgent.Validate(output, Constraints());
        Assert.NotNull(error);
        Assert.Contains("preferred", error);
    }

    [Fact]
    public void WebOnlyCitations_AreFineAtTierB()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("B", false, "80326-98-3", WebCite)] };
        Assert.Null(DiscoveryAgent.Validate(output, Constraints()));
    }

    // A web hit that is CORROBORATED by the catalog is no longer a web-only claim — that is exactly the
    // behaviour the tool description asks for, so it must be allowed at Tier A.
    [Fact]
    public void WebCitationCorroboratedByTheCatalog_MayBeTierA()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("A", true, "80326-98-3", WebCite, CatalogCite)] };
        Assert.Null(DiscoveryAgent.Validate(output, Constraints()));
    }

    // RAIL 2. A CAS with a bad check digit is provably wrong, and a wrong CAS silently clears the WRONG
    // substance through the regulatory gate.
    [Fact]
    public void InvalidCasCheckDigit_IsRejected()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("B", false, "80326-98-4", CatalogCite)] };
        var error = DiscoveryAgent.Validate(output, Constraints());
        Assert.NotNull(error);
        Assert.Contains("check digit", error);
    }

    [Fact]
    public void ValidCas_Passes()
    {
        var output = new DiscoveryOutput { Substances = [Candidate("A", true, "80326-98-3", CatalogCite)] };
        Assert.Null(DiscoveryAgent.Validate(output, Constraints()));
    }
```

- [ ] **Step 2: Run — it must fail**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter DiscoveryAgentTests
```
Expected: FAIL — the rails do not exist.

- [ ] **Step 3: Add the rails to `Validate`**

In `src/Smx.Orchestrator/Agents/DiscoveryAgent.cs`, inside the existing `foreach (var s in o.Substances)` loop, after the citation check:

```csharp
            // RAIL 1 — the web may SUGGEST a marker; only the catalog and the reference corpus may ENDORSE
            // one. Tier A and `preferred` are endorsements. Enforced here, in code, rather than in the
            // prompt: Citation is four free-form strings and nothing else in the pipeline would ever notice.
            var webOnly = s.Citations.All(c => c.Source.StartsWith("web:", StringComparison.OrdinalIgnoreCase));
            if (webOnly && s.Tier == "A")
                return $"candidate '{s.Element}/{s.Form}' is cited only by web sources and cannot be Tier A — " +
                       "corroborate it with search_catalog or search_reference, or tier it B with the limitation named in the rationale";
            if (webOnly && s.Preferred)
                return $"candidate '{s.Element}/{s.Form}' is cited only by web sources and cannot be marked preferred — " +
                       "a preferred form must rest on a catalog or reference source";

            // RAIL 2 — a CAS carries a check digit, so a transposed digit is PROVABLY wrong. A wrong CAS
            // clears the wrong substance through the regulatory gate, doses against the wrong molecular
            // weight, and gets ordered. This is the cheapest guard we have against the headline harm.
            if (!CasNumber.IsValid(s.Cas))
                return $"candidate '{s.Element}/{s.Form}' has CAS '{s.Cas}', which fails its check digit — " +
                       "re-read the CAS from a retrieved source; do not transcribe it from memory";
```

Add `using Smx.Domain;` at the top if it is not already there.

- [ ] **Step 4: Update the instructions**

Replace the tool-list section of `DiscoveryAgent.Instructions` with:
```
        You may only use facts from your tools:
        - search_catalog(element) FIRST — the SMX catalog is the authoritative source for a CAS.
        - search_web(query, intent) ONLY when the catalog does not carry a form you have good reason to
          believe exists. It is a starting point, not an authority. Its results may suggest a candidate; they
          may never endorse one. Corroborate anything you find against search_catalog / search_reference.
          The query must contain NO client, product or project name — only chemistry.
        - search_reference for solubility / XRF cleanliness / form ranking evidence.
        - lookup_compatibility(element, substrate) as a tiering signal (incompatible ⇒ lower tier or C).
        - search_learned_conclusions when tiering an element/form. A higher-confidence, more recent
          conclusion supersedes an older one. If the tool returns no matches, tier from the primary sources —
          do not fabricate a prior finding.
        NEVER state a CAS you did not read from a retrieved source; a CAS is check-digit validated and a
        wrong one will be rejected. A candidate whose citations are ALL web sources must be Tier B, must not
        be preferred, and must name that limitation in its rationale.
        If a tool tells you the external search failed or was refused, that is NOT evidence that no such
        marker exists — say so, and continue from the catalog.
```

- [ ] **Step 5: Run — it must pass**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter DiscoveryAgentTests
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Orchestrator/Agents/DiscoveryAgent.cs src/Smx.Orchestrator.Tests/DiscoveryAgentTests.cs
git commit -m "feat(discovery): the web-evidence rails — Tier ceiling + CAS check digit

Citation is four free-form strings and every validator only checks non-emptiness,
so adding search_web is SUFFICIENT to make unvetted web claims flow into verdicts
with no compile error and no test failure. These two rails are the guardrail that
absence would have left missing."
```

---

## Task 16: Orchestrator DI + options

**Files:**
- Modify: `src/Smx.Infrastructure/BackendOptions.cs`
- Modify: `src/Smx.Orchestrator/Program.cs`
- Test: `src/Smx.Orchestrator.Tests/OrchestratorHostWiringTests.cs` (add a case)

- [ ] **Step 1: Extend `BackendOptions`**

Add four properties to the `BackendOptions` record (append to the parameter list, before the closing paren):
```csharp
    string SearchProxyEndpoint,
    string SearchProxyAudience,
    bool WebSearchEnabled,
    int WebSearchMaxPerStage,
```
And to `From(IConfiguration c)`:
```csharp
        SearchProxyEndpoint: c["SEARCH_PROXY_ENDPOINT"] ?? "",
        SearchProxyAudience: c["SEARCH_PROXY_AUDIENCE"] ?? "",
        // The operator kill switch. Default ON, but an empty endpoint disables it anyway (see Program.cs) —
        // so a deployment that has not been given a proxy simply never searches the web, rather than failing.
        WebSearchEnabled: !bool.TryParse(c["WEB_SEARCH_ENABLED"], out var we) || we,
        WebSearchMaxPerStage: int.TryParse(c["WEB_SEARCH_MAX_PER_STAGE"], out var wm) ? wm : 8,
```

- [ ] **Step 2: Register the factory in `src/Smx.Orchestrator/Program.cs`**

Next to the other tool registrations (near the `SearchClient` registrations around line 109-115):
```csharp
// Web search. The tool is built PER PROJECT (it closes over that project's sensitive terms and its own stage
// budget), so what DI holds is a factory, not an instance.
//
// Fail-safe by construction: with no endpoint configured there is no proxy to call, so the tool reports
// itself disabled and Discovery falls back to the catalog. A missing deployment must degrade the system, not
// break it — and it must never silently egress instead.
var webEnabled = opts.WebSearchEnabled && !string.IsNullOrEmpty(opts.SearchProxyEndpoint);

// SearchProxyClient takes (HttpClient, TokenCredential, endpoint, audience, ILogger). The two strings mean a
// typed-client registration cannot construct it, so name the client and build it explicitly.
services.AddHttpClient(nameof(SearchProxyClient));
services.AddSingleton<ISearchProxyClient>(sp => new SearchProxyClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(SearchProxyClient)),
    sp.GetRequiredService<TokenCredential>(),
    opts.SearchProxyEndpoint,
    opts.SearchProxyAudience,
    sp.GetRequiredService<ILogger<SearchProxyClient>>()));

services.AddSingleton<Func<SensitiveTerms, IWebSearch>>(sp => terms => new WebSearchTool(
    sp.GetRequiredService<ISearchProxyClient>(),
    terms,
    webEnabled,
    opts.WebSearchMaxPerStage,
    sp.GetRequiredService<ILogger<WebSearchTool>>()));
```

The `TokenCredential` singleton is already registered in this file (`Program.cs:56-58`) — reuse it, do not construct a second one.

Add the `using` directives: `using Smx.Domain.Tools;` and `using Smx.Infrastructure.Search;`.

- [ ] **Step 3: Add a wiring test**

Add to `src/Smx.Orchestrator.Tests/OrchestratorHostWiringTests.cs`:
```csharp
    [Fact]
    public void WebSearchFactory_IsRegistered_AndBuildsAPerProjectTool()
    {
        var sp = BuildServices();  // reuse whatever this file's existing builder helper is called
        var factory = sp.GetRequiredService<Func<Smx.Domain.Tools.SensitiveTerms, Smx.Domain.Tools.IWebSearch>>();
        Assert.NotNull(factory(new Smx.Domain.Tools.SensitiveTerms(["Acme"])));
    }
```
> Read the existing file first and reuse its in-memory-configuration helper verbatim; do not add a second one.

- [ ] **Step 4: Build + full test run**

```bash
dotnet build src/Smx.Backend.sln && dotnet test src/Smx.Backend.sln
```
Expected: `Build succeeded`; all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Infrastructure/BackendOptions.cs src/Smx.Orchestrator/Program.cs src/Smx.Orchestrator.Tests
git commit -m "feat(orchestrator): wire the web-search tool factory — fail-safe when no proxy is configured"
```

---

## Task 17: Infrastructure (Bicep) — both topologies

`infra/` (multi-RG) and `infra/single-rg/` are twins. **Both must change**, or the single-rg deploy silently drifts.

**Files:**
- Modify: `infra/modules/functions.bicep`, `infra/single-rg/modules/functions.bicep`
- Modify: `infra/modules/compute.bicep`, `infra/single-rg/modules/compute.bicep`
- Modify: `infra/main.bicep`, `infra/single-rg/main.bicep` (thread the new params)

- [ ] **Step 1: Add the proxy's app settings + cache container + Easy Auth**

In **both** `functions.bicep` files:

New params (beside the existing `authClientId`):
```bicep
@description('Entra app-registration client id for the Search Proxy Easy Auth. Empty = auth stays OFF (first deploy).')
param proxyAuthClientId string = ''

@description('Key Vault secret URI holding the search provider API key. Empty = the proxy answers 503 until it is set.')
param proxySearchKeySecretUri string = ''

@description('Search Proxy knobs.')
param proxyCoverCount int = 4
param proxyMonthlyQueryCap int = 5000
param proxyDryRun bool = false
```

The cache/quota blob container on the proxy's own storage (beside `spDeploy`):
```bicep
// The search-result cache AND the monthly quota counter. On the proxy's OWN storage account, where its
// identity already holds Blob Data Owner — no new RBAC, and in particular no path to the corpus.
resource spCache 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: spBlob
  name: 'search-cache'
}
```

Replace the `searchProxyApp`'s `appSettings` array with:
```bicep
      appSettings: [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'AzureWebJobsStorage__accountName', value: spStorage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: searchProxyUami.properties.clientId }
        { name: 'WORKLOAD_UAMI_CLIENT_ID', value: searchProxyUami.properties.clientId }
        { name: 'PROXY_PROVIDER', value: 'brave' }
        // A Key Vault reference — the key is never a plaintext app setting. Empty until set-search-key.sh runs.
        { name: 'PROXY_SEARCH_API_KEY', value: empty(proxySearchKeySecretUri) ? '' : '@Microsoft.KeyVault(SecretUri=${proxySearchKeySecretUri})' }
        { name: 'PROXY_DRY_RUN', value: string(proxyDryRun) }
        { name: 'PROXY_COVER_COUNT', value: string(proxyCoverCount) }
        { name: 'PROXY_COVER_CORPUS_PATH', value: 'Config/cover-corpus.json' }
        { name: 'PROXY_CACHE_CONTAINER', value: 'search-cache' }
        { name: 'PROXY_MONTHLY_QUERY_CAP', value: string(proxyMonthlyQueryCap) }
      ]
```

Easy Auth on the proxy (beside `regSyncAuth`):
```bicep
// The Search Proxy gets its OWN app registration, not regsync's: they are separate apps with separate
// identities precisely so a compromise of the internet-facing one cannot reach the corpus, and sharing an
// audience would hand it a token the other accepts.
resource searchProxyAuth 'Microsoft.Web/sites/config@2024-04-01' = if (!empty(proxyAuthClientId)) {
  parent: searchProxyApp
  name: 'authsettingsV2'
  properties: {
    platform: { enabled: true }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'Return401'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          openIdIssuer: 'https://login.microsoftonline.com/${subscription().tenantId}/v2.0'
          clientId: proxyAuthClientId
        }
        validation: {
          allowedAudiences: [ 'api://${proxyAuthClientId}' ]
        }
      }
    }
    login: { tokenStore: { enabled: false } }
  }
}
```

Add an output so `main.bicep` can feed the orchestrator:
```bicep
output searchProxyDefaultHostName string = searchProxyApp.properties.defaultHostName
```

- [ ] **Step 2: Grant the proxy identity read on the ONE Key Vault secret**

The proxy needs the Brave key and nothing else. Grant `Key Vault Secrets User` **scoped to the secret**, not the vault. Put this in whichever module owns the Key Vault (`security.bicep`), taking `searchProxyUamiPrincipalId` as a param — `functions.bicep` already outputs it:

```bicep
@description('Principal id of the Search Proxy identity — granted read on the search-key secret ONLY.')
param searchProxyUamiPrincipalId string = ''

@description('Name of the secret holding the search provider API key.')
param searchKeySecretName string = 'search-provider-key'

var kvSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6' // Key Vault Secrets User

resource searchKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: keyVault
  name: searchKeySecretName
}

// Scoped to the SECRET, not the vault. The proxy is the internet-facing component; it gets the one value it
// needs and no read on anything else in the vault.
resource proxySecretRead 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(searchProxyUamiPrincipalId)) {
  name: guid(searchKeySecret.id, searchProxyUamiPrincipalId, kvSecretsUserRoleId)
  scope: searchKeySecret
  properties: {
    principalId: searchProxyUamiPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalType: 'ServicePrincipal'
  }
}
```
> The `existing` secret reference requires the secret to be created first — `set-search-key.sh` (Task 18) does that. Guard the whole block on a `deploySearchKeyRbac bool = false` param if a fresh subscription would otherwise fail on the missing secret, and set it to `true` on the redeploy after the key is set. **Read `security.bicep` first and match how it names the vault resource.**

- [ ] **Step 3: Orchestrator env vars**

In **both** `compute.bicep` files, add to the orchestrator container's `env` array (beside `FOUNDRY_ENDPOINT`):
```bicep
  { name: 'SEARCH_PROXY_ENDPOINT', value: searchProxyEndpoint }
  { name: 'SEARCH_PROXY_AUDIENCE', value: searchProxyAudience }
  { name: 'WEB_SEARCH_ENABLED', value: string(webSearchEnabled) }
  { name: 'WEB_SEARCH_MAX_PER_STAGE', value: string(webSearchMaxPerStage) }
```
with new params:
```bicep
@description('Search Proxy base URL (https://<app>.azurewebsites.net), reached over its private endpoint.')
param searchProxyEndpoint string = ''
@description('Entra audience of the Search Proxy (api://<proxyAuthClientId>).')
param searchProxyAudience string = ''
param webSearchEnabled bool = true
param webSearchMaxPerStage int = 8
```

- [ ] **Step 4: Thread the params through `main.bicep` (both)**

Pass `searchProxyEndpoint: 'https://${functions.outputs.searchProxyDefaultHostName}'` and `searchProxyAudience: empty(proxyAuthClientId) ? '' : 'api://${proxyAuthClientId}'` into the compute module, and declare `proxyAuthClientId` / `proxySearchKeySecretUri` / `proxyDryRun` as top-level params forwarded to the functions module.
> **Ordering:** the compute module must now depend on the functions module's output. Check that this does not create a cycle — `functions.bicep` does not consume anything from `compute.bicep`, so it should not.

- [ ] **Step 5: Both Bicep variants must compile**

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null && echo "multi-rg OK"
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null && echo "single-rg OK"
```
Expected: both print OK, with no warnings about unused params.

- [ ] **Step 6: Commit**

```bash
git add infra/
git commit -m "infra(proxy): app settings, Easy Auth, cache container, per-secret KV grant

The proxy's Key Vault grant is scoped to the ONE secret it needs, not the vault:
it is the internet-facing component, and the whole point of its dedicated
identity is that a compromise reaches nothing."
```

---

## Task 18: Scripts (bash + PowerShell twins — fix a bug in one, fix it in the other)

**Files:**
- Create: `infra/scripts/publish-searchproxy.sh` + `.ps1`
- Create: `infra/scripts/set-search-key.sh` + `.ps1`
- Modify: `infra/scripts/configure-auth.sh` + `.ps1`

- [ ] **Step 1: `publish-searchproxy.sh`**

Model it exactly on `publish-functions.sh` (read that file first — it handles the Windows native-path quirk via `to_native_path`):
```bash
#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Build + zip-deploy the Smx.SearchProxy project to the searchproxy Function App.
# It is a SEPARATE project from Smx.Functions on purpose: this app's identity has no corpus RBAC, and
# deploying the SDS/Reg code here would drag Cosmos/Bronze/Search dependencies onto the internet-facing app.
ENV="$(require_env_arg "${1:-}")"
confirm_subscription
RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"
APP="func-${NAME_PREFIX}-${ENV}-searchproxy-${REGION_SHORT}"
PROJ="${INFRA_DIR}/../src/Smx.SearchProxy"
OUT="$(mktemp -d)"; ZIP="${OUT}/search-proxy.zip"

require_cmd dotnet
log "Publishing ${PROJ} -> ${APP} (${RG})"
dotnet publish "${PROJ}" -c Release -o "${OUT}/publish"
make_zip "${OUT}/publish" "${ZIP}"
az functionapp deployment source config-zip -g "${RG}" -n "${APP}" --src "$(to_native_path "${ZIP}")" --output none
rm -rf "${OUT}"
log "Published the Search Proxy to ${APP}. (Run set-search-key.sh and configure-auth.sh next.)"
```
Write the `.ps1` twin with the same steps and the same guards, ASCII-only (see `infra/scripts/README.md`).

- [ ] **Step 2: `set-search-key.sh`**

```bash
#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Put the search provider API key in Key Vault and print the secret URI to feed back into Bicep.
# The key is NEVER a plaintext app setting: the proxy reads it through a Key Vault reference.
ENV="$(require_env_arg "${1:-}")"
KEY="${2:-}"
[ -n "${KEY}" ] || { echo "usage: set-search-key.sh <env> <brave-api-key>" >&2; exit 1; }
confirm_subscription

KV="kv-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"   # confirm against infra/modules/security.bicep
SECRET="search-provider-key"

log "Storing the search provider key in ${KV}/${SECRET}..."
URI="$(az keyvault secret set --vault-name "${KV}" --name "${SECRET}" --value "${KEY}" --query id -o tsv)"
log "Secret URI: ${URI}"
warn "Redeploy with -p proxySearchKeySecretUri=${URI} -p deploySearchKeyRbac=true, then re-run harden.sh."
```
> Read `infra/modules/security.bicep` and use the **actual** Key Vault name pattern; do not guess it.

Write the `.ps1` twin.

- [ ] **Step 3: Extend `configure-auth.sh` (and its twin) with a second app registration**

Append to `configure-auth.sh`, after the existing regsync block:
```bash
# --- Search Proxy: its OWN app registration. Separate identities, separate audiences: sharing regsync's
#     audience would hand the internet-facing proxy a token the corpus-writing app accepts. ---
PROXY_APP="func-${NAME_PREFIX}-${ENV}-searchproxy-${REGION_SHORT}"
PROXY_REG_NAME="${NAME_PREFIX}-${ENV}-searchproxy-auth"

log "Ensuring Entra app registration '${PROXY_REG_NAME}'..."
PROXY_CLIENT_ID="$(az ad app list --display-name "${PROXY_REG_NAME}" --query '[0].appId' -o tsv)"
if [ -z "${PROXY_CLIENT_ID}" ]; then
  PROXY_CLIENT_ID="$(az ad app create --display-name "${PROXY_REG_NAME}" \
    --identifier-uris "api://${PROXY_REG_NAME}" --query appId -o tsv)"
  log "Created app registration ${PROXY_CLIENT_ID}"
fi

log "Enforcing Easy Auth on ${PROXY_APP} (audience api://${PROXY_REG_NAME})..."
az webapp auth update -g "${RG}" -n "${PROXY_APP}" \
  --enabled true --action Return401 \
  --aad-allowed-token-audiences "api://${PROXY_REG_NAME}" \
  --aad-client-id "${PROXY_CLIENT_ID}" \
  --aad-token-issuer-url "https://login.microsoftonline.com/${TENANT_ID}/v2.0" --output none

warn "The ACA orchestrator must present a token for audience api://${PROXY_REG_NAME} (SEARCH_PROXY_AUDIENCE)."
log "Keep Bicep in sync: redeploy with -p proxyAuthClientId=${PROXY_CLIENT_ID}."
```
Mirror it in the `.ps1`.

- [ ] **Step 4: Verify the twins**

```bash
bash -n infra/scripts/publish-searchproxy.sh && bash -n infra/scripts/set-search-key.sh && bash -n infra/scripts/configure-auth.sh && echo "bash syntax OK"
pwsh -NoProfile -Command "\$null = [ScriptBlock]::Create((Get-Content -Raw infra/scripts/publish-searchproxy.ps1)); 'ps1 parse OK'"
```
Expected: both OK. (If `pwsh` is unavailable, parse-check the `.ps1` files however `infra/scripts/README.md` recommends.)

- [ ] **Step 5: Commit**

```bash
git add infra/scripts/
git commit -m "infra(scripts): publish-searchproxy + set-search-key twins; Easy Auth for the proxy"
```

---

## Task 19: Documentation

**Files:**
- Modify: `docs/superpowers/specs/2026-07-12-chemistry-backend-end-to-end-design.md:59,117`
- Modify: `CLAUDE.md`
- Modify: `infra/README.md`

- [ ] **Step 1: Correct the "NO open web" claim**

Both places attribute the ban to the HLD, and the HLD says the opposite — it provisions "Search Proxy (anonymized public search)" as a first-class component. Replace:
- line ~59 (the flow diagram annotation): `Universe = seeded catalog + knowledge layer. NO open web (HLD).`
  → `Universe = seeded catalog + knowledge layer + anonymized web search via the Search Proxy (HLD).`
- line ~117 (Discovery's correctness rails): `universe bounded to catalog + knowledge layer (no open web, HLD)`
  → `universe bounded to catalog + knowledge layer, extended by anonymized web search through the Search Proxy (HLD). Web-only candidates are capped at Tier B and can never be preferred; Regulatory has no web tool at all.`

Add a note under the section pointing at the new spec.

- [ ] **Step 2: `CLAUDE.md` — add the subsystem**

Under "Application code", after the Reference-data subsystem bullet:
```markdown
- **Search Proxy** (`src/Smx.SearchProxy`, .NET 8 isolated worker; deployed into the `searchproxy`
  Function App — a SEPARATE app and identity from `regsync`, with zero corpus RBAC) — the anonymizing
  external-search egress. It answers *live search queries* and deliberately has **no fetch interface**, so
  third-party hosts never see us. Each real query egresses inside a shuffled batch of decoys drawn from a
  git-versioned corpus of the catalog's chemistry (k-anonymity), the request contract is **project-blind**
  (there is no field a project id could travel in, and strict binding rejects one), and every request is
  audited to App Insights. Its only consumer is the **Discovery** agent's `search_web` tool — the
  Regulatory agent has no web tool and never will. Design + plan:
  [`docs/superpowers/specs/2026-07-13-search-proxy-design.md`](docs/superpowers/specs/2026-07-13-search-proxy-design.md),
  [`docs/superpowers/plans/2026-07-13-search-proxy.md`](docs/superpowers/plans/2026-07-13-search-proxy.md).
  - Build/test: `dotnet build src/Smx.Functions.sln` · `dotnet test src/Smx.Functions.sln`
  - Regenerate the decoy corpus: `dotnet run --project tools/Smx.CoverCorpus -- src/Smx.Functions/Reference/Seed src/Smx.SearchProxy/Config/cover-corpus.json`
  - Deploy: `infra/scripts/publish-searchproxy.sh <env>`, `infra/scripts/set-search-key.sh <env> <key>`,
    then `infra/scripts/configure-auth.sh <env>`.
  - Run with no key and no egress: `PROXY_DRY_RUN=true`.
```

- [ ] **Step 3: `infra/README.md`**

The proxy is no longer an empty shell. Document `publish-searchproxy.*`, `set-search-key.*`, the new deploy-order step (publish → set key → configure-auth → redeploy with `proxyAuthClientId` + `proxySearchKeySecretUri` → harden), and the `PROXY_*` knobs.

- [ ] **Step 4: Full verification, then commit**

```bash
dotnet build src/Smx.Functions.sln && dotnet test src/Smx.Functions.sln
dotnet build src/Smx.Backend.sln  && dotnet test src/Smx.Backend.sln
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```
Expected: all green.

```bash
git add CLAUDE.md infra/README.md docs/
git commit -m "docs: the Search Proxy is real — correct the 'NO open web' misreading of the HLD"
```

---

## Definition of done

- [ ] Both solutions build; all tests pass; both Bicep variants compile.
- [ ] `PROXY_DRY_RUN=true` runs the proxy end to end with no key and no network.
- [ ] Every hard invariant in spec §3 has a passing test:
      1. single upstream host · 2. no fetch interface · 3. project-blind (strict binding) ·
      4. no real query egresses alone · 5. Regulatory has no web tool · 6. every request audited ·
      7. a web-only candidate is never Tier A and never preferred.
- [ ] The seeded catalog's CAS numbers all pass their check digit (`SeedCasIntegrityTests`).
- [ ] `search_web` appears in `DiscoveryTools` and in **no** other tool list.

---

## Deferred (deliberately — see spec §12)

- **Cost as a consumer** (Plan 4). It is the *leakiest*: "Yb neodecanoate 10g price" is far more specific than a literature question and needs its own decoy strategy. The `intent` enum exists so that work does not reopen the proxy.
- **An operator-facing search box.** The argument for it is real: an operator who cannot search *through* the proxy will search from their laptop, which leaks worse and leaves no audit trail.
- **Stylometric decoys.** Template-generated decoys may be distinguishable from model-written queries. The fix is to generate decoys with the same model, or to seed the corpus from past real queries with the chemistry substituted.
- **The `data/` workbook still carries the bad Sc CAS.** Task 0 fixes the generated seed JSON; a future `Smx.ReferenceData.Transform` run would reintroduce `15492-49-8`. `SeedCasIntegrityTests` will catch it — but the workbook needs the correction at source, which is an operator action.
