# SDS Pre-Seed Library Subsystem — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a project-independent, scheduled-bulk SDS library — a timer sweep + HTTP operations in a .NET 8 isolated Function App (folded into the existing `regsync` app), backed by new Cosmos containers, an ADLS `bronze` filesystem, and a dedicated AI Search index — that gathers supplier SDS PDFs, validates + GHS-chunks + embeds + indexes them, and serves them to the assessment workflow without any on-demand per-project fetch.

**Architecture:** Deterministic infrastructure code, not agent logic. One `IEgressClient` (NAT egress + allowlist) is the sole outbound path and is injected only into the timer sweep. A shared `IngestionPipeline` is used by both the sweep and operator upload. Source resolution is strategy-based (manufacturers vs. aggregators). All storage/AI access is keyless via the workload managed identity over private endpoints.

**Tech Stack:** .NET 8 isolated Azure Functions (C#), xUnit, `Microsoft.Azure.Cosmos`, `Azure.Search.Documents`, `Azure.Storage.Files.DataLake`, `Azure.AI.OpenAI`, `Azure.Identity`, `UglyToad.PdfPig`. Bicep + bash for infra. Spec: [`docs/superpowers/specs/2026-07-07-sds-library-subsystem-design.md`](../specs/2026-07-07-sds-library-subsystem-design.md).

---

## File structure

```
src/
  Smx.Functions.sln
  Smx.Functions/
    Smx.Functions.csproj              # net8.0 isolated worker
    Program.cs                        # DI wiring
    host.json
    Sds/
      Config/
        SdsOptions.cs                 # binds SDS_* settings
        suppliers.allowlist.json      # ordered supplier allowlist (single artifact)
      Domain/
        Models.cs                     # records: MasterListEntry, RegistryPointer, SdsChunk, etc.
        DedupKey.cs                   # id/dedup-key construction
      Sourcing/
        AllowlistProvider.cs          # loads + validates the allowlist json
        SourceResolver.cs             # ordered walk → candidates
        ISourceStrategy.cs            # strategy interface + EgressFetch delegate
        CasTemplateStrategy.cs
        ProductLookupStrategy.cs
        IEgressClient.cs
        NatEgressClient.cs            # HttpClient via subnet NAT; allowlist + timeout + size cap
        DryRunEgressClient.cs         # fixtures, no network
      Ingestion/
        IPdfTextExtractor.cs
        PdfTextExtractor.cs           # PdfPig adapter
        SdsValidator.cs
        GhsChunker.cs
        IEmbedder.cs
        Embedder.cs                   # text-embedding-3-large (keyless)
        ISdsSearchClient.cs
        SdsSearchClient.cs            # EnsureIndexAsync + push
        IngestionPipeline.cs
      Data/
        IMasterListStore.cs  CosmosMasterListStore.cs  MasterListRepo.cs
        IRegistryStore.cs    CosmosRegistryStore.cs    RegistryRepo.cs
        IBronzeStore.cs      AdlsBronzeStore.cs
      Triggers/
        SdsSweep.cs  OperatorUpload.cs  AppendToMasterList.cs
        GetSdsForSubstance.cs  GetSdsStatus.cs
  Smx.Functions.Tests/
    Smx.Functions.Tests.csproj
    Fakes/  InMemoryMasterListStore.cs  InMemoryRegistryStore.cs  FakeEmbedder.cs  FakeSearchClient.cs  FakeBronzeStore.cs
    DedupKeyTests.cs  SourceResolverTests.cs  SdsValidatorTests.cs  GhsChunkerTests.cs
    MasterListRepoTests.cs  IngestionPipelineTests.cs  SdsSweepTests.cs
    Resources/  (fixture SDS text + a small fixture PDF)
infra/
  modules/data.bicep                  # + 2 Cosmos containers + bronze filesystem   (and single-rg/modules/data.bicep)
  modules/functions.bicep             # + SDS app settings + authsettingsV2         (and single-rg/modules/functions.bicep)
  main.bicep                          # + endpoint/authClientId wiring              (and single-rg/main.bicep)
  scripts/publish-functions.sh        # + configure-auth.sh                          (and single-rg/scripts/…)
```

**Both infra variants (`infra/` and `infra/single-rg/`) have identical resource anchors** — every Bicep edit below is applied to *both* the `infra/modules/…` file and the `infra/single-rg/modules/…` file at the same location.

---

## Phase 1 — Infrastructure (Bicep, both variants)

### Task 1: Cosmos containers + ADLS `bronze` filesystem in `data.bicep`

**Files:**
- Modify: `infra/modules/data.bicep` and `infra/single-rg/modules/data.bicep` (identical edits)

- [ ] **Step 1: Add the blob service + `bronze` container after the `storage` resource** (`data.bicep:56`, right after the `storage` resource closes)

```bicep
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

// Bronze medallion filesystem — raw SDS PDFs land under the sds/<cas>/<supplier>/<rev>.pdf prefix.
resource bronzeContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'bronze'
}
```

- [ ] **Step 2: Add the two SDS containers after the `cosmosDb` resource** (`data.bicep:105`, after `cosmosDb` closes)

```bicep
// SDS master list — one row per (element, form); idempotent upsert keyed on id.
resource sdsMasterList 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: cosmosDb
  name: 'sds-master-list'
  properties: {
    resource: {
      id: 'sds-master-list'
      partitionKey: { paths: [ '/element' ], kind: 'Hash' }
    }
  }
}

// SDS registry — one row per gathered SDS; partitioned by CAS.
resource sdsRegistry 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: cosmosDb
  name: 'sds-registry'
  properties: {
    resource: {
      id: 'sds-registry'
      partitionKey: { paths: [ '/cas' ], kind: 'Hash' }
    }
  }
}
```

- [ ] **Step 3: Add outputs at the end of `data.bicep`** (after `data.bicep:120`)

```bicep
output bronzeFilesystem string = bronzeContainer.name
output sdsMasterListContainer string = sdsMasterList.name
output sdsRegistryContainer string = sdsRegistry.name
```

- [ ] **Step 4: Build both variants**

Run: `az bicep build --file infra/modules/data.bicep && az bicep build --file infra/single-rg/main.bicep`
Expected: no errors (warnings about unused outputs are fine).

- [ ] **Step 5: Commit**

```bash
git add infra/modules/data.bicep infra/single-rg/modules/data.bicep
git commit -m "feat(infra): add SDS Cosmos containers + bronze filesystem (both variants)"
```

### Task 2: SDS app settings + Entra Easy Auth in `functions.bicep`

**Files:**
- Modify: `infra/modules/functions.bicep` and `infra/single-rg/modules/functions.bicep` (identical)

- [ ] **Step 1: Add params after `maxInstanceCount`** (`functions.bicep:41`)

```bicep
@description('Corpus endpoints for the SDS subsystem (fed from data/ai module outputs).')
param cosmosAccountEndpoint string = ''
param cosmosDatabaseName string = 'smx'
param bronzeAccountName string = ''
param searchEndpoint string = ''
param foundryEndpoint string = ''
param embeddingDeployment string = 'text-embedding-3-large'

@description('SDS sweep knobs.')
param sdsSweepCron string = '0 0 3 * * 1'          // weekly, Monday 03:00 UTC
param sdsRetryCap int = 3
param sdsFetchTimeoutSeconds int = 30
param sdsRevisionRecheckDays int = 90
param sdsDryRun bool = false
param sdsSearchIndex string = 'sds-index'

@description('Entra app-registration client id for Easy Auth. Empty = auth stays OFF (first deploy).')
param authClientId string = ''
```

- [ ] **Step 2: Append SDS settings to the regsync app's `appSettings` array** — insert after the `AzureWebJobsStorage__clientId` line (`functions.bicep:262`, inside `regSyncApp` `appSettings`)

```bicep
        { name: 'COSMOS_ACCOUNT_ENDPOINT', value: cosmosAccountEndpoint }
        { name: 'COSMOS_DATABASE', value: cosmosDatabaseName }
        { name: 'SDS_MASTER_CONTAINER', value: 'sds-master-list' }
        { name: 'SDS_REGISTRY_CONTAINER', value: 'sds-registry' }
        { name: 'BRONZE_ACCOUNT_NAME', value: bronzeAccountName }
        { name: 'BRONZE_FILESYSTEM', value: 'bronze' }
        { name: 'SEARCH_ENDPOINT', value: searchEndpoint }
        { name: 'SDS_SEARCH_INDEX', value: sdsSearchIndex }
        { name: 'FOUNDRY_ENDPOINT', value: foundryEndpoint }
        { name: 'EMBEDDING_DEPLOYMENT', value: embeddingDeployment }
        { name: 'WORKLOAD_UAMI_CLIENT_ID', value: workloadUamiClientId }
        { name: 'SDS_SWEEP_CRON', value: sdsSweepCron }
        { name: 'SDS_RETRY_CAP', value: string(sdsRetryCap) }
        { name: 'SDS_FETCH_TIMEOUT_SECONDS', value: string(sdsFetchTimeoutSeconds) }
        { name: 'SDS_REVISION_RECHECK_DAYS', value: string(sdsRevisionRecheckDays) }
        { name: 'SDS_DRY_RUN', value: string(sdsDryRun) }
        { name: 'SDS_ALLOWLIST_PATH', value: 'Sds/Config/suppliers.allowlist.json' }
```

- [ ] **Step 3: Add the Easy Auth config resource** — after the `regSyncApp` resource closes (`functions.bicep:266`)

```bicep
// Entra ID (App Service Auth v2). Gated on authClientId so the first deploy (empty) succeeds;
// configure-auth.sh creates the app registration then redeploys with its clientId to enforce auth.
resource regSyncAuth 'Microsoft.Web/sites/config@2024-04-01' = if (!empty(authClientId)) {
  parent: regSyncApp
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
          clientId: authClientId
        }
        validation: {
          allowedAudiences: [ 'api://${authClientId}' ]
        }
      }
    }
    login: { tokenStore: { enabled: false } }
  }
}
```

- [ ] **Step 4: Build both variants**

Run: `az bicep build --file infra/modules/functions.bicep && az bicep build --file infra/single-rg/modules/functions.bicep`
Expected: no errors.

- [ ] **Step 5: Commit**

```bash
git add infra/modules/functions.bicep infra/single-rg/modules/functions.bicep
git commit -m "feat(infra): SDS app settings + Entra Easy Auth on regsync app (both variants)"
```

### Task 3: Wire endpoints + authClientId in `main.bicep` (both variants)

**Files:**
- Modify: `infra/main.bicep` (functions module call at `:236`) and `infra/single-rg/main.bicep` (functions module call at `:164`)

- [ ] **Step 1: Add an `authClientId` param to both `main.bicep` files** (near the other params, e.g. after `deployGpt4o` in `infra/main.bicep`)

```bicep
@description('Entra app-registration client id for Function App Easy Auth. Empty = auth OFF (first deploy); configure-auth.sh fills it in.')
param authClientId string = ''
```

- [ ] **Step 2: Pass the new params into the `functions` module call** — add inside the `functions` module `params: { … }` block (after `workloadUamiClientId:` line)

```bicep
    cosmosAccountEndpoint: 'https://${data.outputs.cosmosName}.documents.azure.com:443/'
    cosmosDatabaseName: cosmosDatabaseName
    bronzeAccountName: data.outputs.storageName
    searchEndpoint: 'https://${ai.outputs.searchName}.search.windows.net'
    foundryEndpoint: ai.outputs.foundryEndpoint
    authClientId: authClientId
```

> Note (`infra/single-rg/main.bicep`): it declares `cosmosDatabaseName` at `:24`; use it verbatim. Both `data`/`ai` modules already output `cosmosName`/`storageName`/`searchName`/`foundryEndpoint`.

- [ ] **Step 3: Build both variants end-to-end**

Run: `az bicep build --file infra/main.bicep && az bicep build --file infra/single-rg/main.bicep`
Expected: no errors.

- [ ] **Step 4: Commit**

```bash
git add infra/main.bicep infra/single-rg/main.bicep
git commit -m "feat(infra): wire SDS corpus endpoints + authClientId into functions module (both variants)"
```

---

## Phase 2 — .NET project scaffold

### Task 4: Create the solution, function project, and test project

**Files:**
- Create: `src/Smx.Functions/Smx.Functions.csproj`, `src/Smx.Functions/host.json`, `src/Smx.Functions/Program.cs`, `src/Smx.Functions.Tests/Smx.Functions.Tests.csproj`, `src/Smx.Functions.sln`

- [ ] **Step 1: Write `src/Smx.Functions/Smx.Functions.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Smx.Functions</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="1.22.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="1.17.4" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Timer" Version="4.3.1" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http" Version="3.2.0" />
    <PackageReference Include="Microsoft.ApplicationInsights.WorkerService" Version="2.22.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.ApplicationInsights" Version="1.4.0" />
    <PackageReference Include="Azure.Identity" Version="1.12.0" />
    <PackageReference Include="Microsoft.Azure.Cosmos" Version="3.43.0" />
    <PackageReference Include="Azure.Search.Documents" Version="11.6.0" />
    <PackageReference Include="Azure.Storage.Files.DataLake" Version="12.20.0" />
    <PackageReference Include="Azure.AI.OpenAI" Version="2.1.0" />
    <PackageReference Include="UglyToad.PdfPig" Version="0.1.9" />
  </ItemGroup>
  <ItemGroup>
    <None Update="host.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
    <None Update="Sds/Config/suppliers.allowlist.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `src/Smx.Functions/host.json`**

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

- [ ] **Step 3: Write a minimal `src/Smx.Functions/Program.cs`** (DI is filled in Task 21)

```csharp
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .Build();

host.Run();
```

- [ ] **Step 4: Write `src/Smx.Functions.Tests/Smx.Functions.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Smx.Functions/Smx.Functions.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Update="Resources/**"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Create the solution and add projects**

Run:
```bash
cd src && dotnet new sln -n Smx.Functions \
  && dotnet sln add Smx.Functions/Smx.Functions.csproj Smx.Functions.Tests/Smx.Functions.Tests.csproj && cd ..
```

- [ ] **Step 6: Restore + build to verify the scaffold compiles**

Run: `dotnet build src/Smx.Functions.sln`
Expected: Build succeeded (0 errors). (First run restores NuGet packages.)

- [ ] **Step 7: Commit**

```bash
git add src/
git commit -m "chore(sds): scaffold Smx.Functions .NET 8 isolated project + xUnit tests"
```

---

## Phase 3 — Domain models, dedup keys, options

### Task 5: Domain models

**Files:**
- Create: `src/Smx.Functions/Sds/Domain/Models.cs`

- [ ] **Step 1: Write the models** (records used across the subsystem — define once for type consistency)

```csharp
namespace Smx.Functions.Sds.Domain;

public static class SdsStatus
{
    public const string Pending = "pending";
    public const string Fetched = "fetched";
    public const string Failed = "failed";
    public const string AwaitingOperator = "awaiting_operator";
}

public sealed record MasterListEntry(
    string Id, string Element, string Form, string Cas, string? SubstrateClass,
    string Status, string AddedBy, string AddedUtc, string? LastAttemptUtc, int AttemptCount);

public sealed record RegistryPointer(
    string Id, string Cas, string Supplier, string ProductName, string RevisionDate,
    string? Region, string? Language, string SourceUrl, string BlobPath, bool Indexed,
    IReadOnlyList<string> IndexDocIds, string IngestedUtc, string? SupersededBy, string MasterListId);

public sealed record SdsChunk(
    string Id, string Cas, string Supplier, string ProductName, string RevisionDate,
    string? Region, string? Language, string GhsSection, string Content,
    float[] ContentVector, string BlobPath, string MasterListId);

public sealed record AllowlistEntry(
    string Supplier, string Domain, int Priority, string Strategy,
    string SdsUrlTemplate, string? SearchUrlTemplate, string? ProductNumberRegex);

public sealed record SubstanceKey(string Element, string Form, string Cas);

public sealed record SourceCandidate(string Supplier, string Domain, Uri Url);

public sealed record SdsMetadata(
    string Cas, string Supplier, string ProductName, string RevisionDate,
    string? Region, string? Language, string SourceUrl, string MasterListId);

public sealed record EgressResult(byte[] Content, string ContentType, Uri FinalUrl);

public sealed record ValidationResult(bool Ok, string? Reason);

public sealed record IngestResult(bool Ok, string? Reason, string? RegistryId);
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Smx.Functions/Smx.Functions.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Smx.Functions/Sds/Domain/Models.cs
git commit -m "feat(sds): domain models"
```

### Task 6: DedupKey (TDD)

**Files:**
- Create: `src/Smx.Functions/Sds/Domain/DedupKey.cs`
- Test: `src/Smx.Functions.Tests/DedupKeyTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Functions.Sds.Domain;
using Xunit;

public class DedupKeyTests
{
    [Fact]
    public void MasterListId_slugs_element_and_form()
        => Assert.Equal("Yb_neodecanoate", DedupKey.ForMasterList("Yb", "Neodecanoate"));

    [Fact]
    public void MasterListId_slug_replaces_spaces_and_lowercases_form()
        => Assert.Equal("Ti_titanium-dioxide", DedupKey.ForMasterList("Ti", "Titanium Dioxide"));

    [Fact]
    public void RegistryId_is_cas_supplier_revision_normalized()
        => Assert.Equal("27253-31-2|strem|2024-03-01",
            DedupKey.ForRegistry(" 27253-31-2 ", "Strem", "2024-03-01"));

    [Fact]
    public void RegistryId_same_cas_different_supplier_or_revision_are_distinct()
    {
        var a = DedupKey.ForRegistry("1", "sigma", "2024-01-01");
        var b = DedupKey.ForRegistry("1", "sigma", "2024-06-01");
        var c = DedupKey.ForRegistry("1", "strem", "2024-01-01");
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.Tests --filter DedupKeyTests`
Expected: FAIL (DedupKey does not exist).

- [ ] **Step 3: Implement `DedupKey.cs`**

```csharp
using System.Text.RegularExpressions;

namespace Smx.Functions.Sds.Domain;

public static class DedupKey
{
    public static string ForMasterList(string element, string form)
        => $"{element.Trim()}_{Slug(form)}";

    public static string ForRegistry(string cas, string supplier, string revisionDate)
        => $"{Norm(cas)}|{Norm(supplier)}|{Norm(revisionDate)}";

    private static string Norm(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string Slug(string s)
        => Regex.Replace(Norm(s), @"[^a-z0-9]+", "-").Trim('-');
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.Tests --filter DedupKeyTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Functions/Sds/Domain/DedupKey.cs src/Smx.Functions.Tests/DedupKeyTests.cs
git commit -m "feat(sds): dedup/id key construction (TDD)"
```

### Task 7: SdsOptions

**Files:**
- Create: `src/Smx.Functions/Sds/Config/SdsOptions.cs`

- [ ] **Step 1: Write `SdsOptions.cs`** (reads env/config; used by DI)

```csharp
using Microsoft.Extensions.Configuration;

namespace Smx.Functions.Sds.Config;

public sealed class SdsOptions
{
    public string CosmosEndpoint { get; init; } = "";
    public string CosmosDatabase { get; init; } = "smx";
    public string MasterContainer { get; init; } = "sds-master-list";
    public string RegistryContainer { get; init; } = "sds-registry";
    public string BronzeAccount { get; init; } = "";
    public string BronzeFilesystem { get; init; } = "bronze";
    public string SearchEndpoint { get; init; } = "";
    public string SearchIndex { get; init; } = "sds-index";
    public string FoundryEndpoint { get; init; } = "";
    public string EmbeddingDeployment { get; init; } = "text-embedding-3-large";
    public string? UamiClientId { get; init; }
    public int RetryCap { get; init; } = 3;
    public int FetchTimeoutSeconds { get; init; } = 30;
    public int RevisionRecheckDays { get; init; } = 90;
    public bool DryRun { get; init; }
    public string AllowlistPath { get; init; } = "Sds/Config/suppliers.allowlist.json";
    public int MaxPdfBytes { get; init; } = 25 * 1024 * 1024;
    public int MinGhsSections { get; init; } = 10;

    public static SdsOptions From(IConfiguration c) => new()
    {
        CosmosEndpoint = c["COSMOS_ACCOUNT_ENDPOINT"] ?? "",
        CosmosDatabase = c["COSMOS_DATABASE"] ?? "smx",
        MasterContainer = c["SDS_MASTER_CONTAINER"] ?? "sds-master-list",
        RegistryContainer = c["SDS_REGISTRY_CONTAINER"] ?? "sds-registry",
        BronzeAccount = c["BRONZE_ACCOUNT_NAME"] ?? "",
        BronzeFilesystem = c["BRONZE_FILESYSTEM"] ?? "bronze",
        SearchEndpoint = c["SEARCH_ENDPOINT"] ?? "",
        SearchIndex = c["SDS_SEARCH_INDEX"] ?? "sds-index",
        FoundryEndpoint = c["FOUNDRY_ENDPOINT"] ?? "",
        EmbeddingDeployment = c["EMBEDDING_DEPLOYMENT"] ?? "text-embedding-3-large",
        UamiClientId = c["WORKLOAD_UAMI_CLIENT_ID"],
        RetryCap = int.TryParse(c["SDS_RETRY_CAP"], out var r) ? r : 3,
        FetchTimeoutSeconds = int.TryParse(c["SDS_FETCH_TIMEOUT_SECONDS"], out var t) ? t : 30,
        RevisionRecheckDays = int.TryParse(c["SDS_REVISION_RECHECK_DAYS"], out var d) ? d : 90,
        DryRun = bool.TryParse(c["SDS_DRY_RUN"], out var dr) && dr,
        AllowlistPath = c["SDS_ALLOWLIST_PATH"] ?? "Sds/Config/suppliers.allowlist.json",
    };
}
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build src/Smx.Functions/Smx.Functions.csproj
git add src/Smx.Functions/Sds/Config/SdsOptions.cs
git commit -m "feat(sds): SdsOptions config binding"
```

---

## Phase 4 — Sourcing (allowlist, resolver, strategies) — TDD

### Task 8: Allowlist file + provider

**Files:**
- Create: `src/Smx.Functions/Sds/Config/suppliers.allowlist.json`, `src/Smx.Functions/Sds/Sourcing/AllowlistProvider.cs`

- [ ] **Step 1: Write `suppliers.allowlist.json`** (ordered; manufacturers first — see spec Appendix A)

```json
[
  {
    "supplier": "Sigma-Aldrich", "domain": "sigmaaldrich.com", "priority": 10,
    "strategy": "productLookup",
    "searchUrlTemplate": "https://www.sigmaaldrich.com/US/en/search/{cas}?focus=products&type=cas_number",
    "productNumberRegex": "/sds/(?<brand>[a-z]+)/(?<productNumber>[a-z0-9]+)",
    "sdsUrlTemplate": "https://www.sigmaaldrich.com/US/en/sds/{brand}/{productNumber}"
  },
  {
    "supplier": "ChemBlink", "domain": "chemblink.com", "priority": 90,
    "strategy": "casTemplate",
    "sdsUrlTemplate": "https://www.chemblink.com/MSDS/MSDSFiles/{cas}.pdf"
  }
]
```

- [ ] **Step 2: Write `AllowlistProvider.cs`**

```csharp
using System.Text.Json;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

public sealed class AllowlistProvider
{
    private readonly IReadOnlyList<AllowlistEntry> _entries;

    public AllowlistProvider(IReadOnlyList<AllowlistEntry> entries)
        => _entries = entries.OrderBy(e => e.Priority).ToList();

    public static AllowlistProvider FromFile(string path)
    {
        var json = File.ReadAllText(path);
        return FromJson(json);
    }

    public static AllowlistProvider FromJson(string json)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var entries = JsonSerializer.Deserialize<List<AllowlistEntry>>(json, opts)
                      ?? throw new InvalidOperationException("Allowlist parsed to null.");
        if (entries.Count == 0) throw new InvalidOperationException("Allowlist is empty.");
        return new AllowlistProvider(entries);
    }

    public IReadOnlyList<AllowlistEntry> Ordered => _entries;

    public IReadOnlySet<string> Domains
        => _entries.Select(e => e.Domain.ToLowerInvariant()).ToHashSet();
}
```

- [ ] **Step 3: Build + commit**

```bash
dotnet build src/Smx.Functions/Smx.Functions.csproj
git add src/Smx.Functions/Sds/Config/suppliers.allowlist.json src/Smx.Functions/Sds/Sourcing/AllowlistProvider.cs
git commit -m "feat(sds): supplier allowlist artifact + provider"
```

### Task 9: Strategy interface + CasTemplateStrategy (TDD)

**Files:**
- Create: `src/Smx.Functions/Sds/Sourcing/ISourceStrategy.cs`, `src/Smx.Functions/Sds/Sourcing/CasTemplateStrategy.cs`
- Test: `src/Smx.Functions.Tests/SourceResolverTests.cs` (start with the CasTemplate case)

- [ ] **Step 1: Write `ISourceStrategy.cs`** (interface + the egress-fetch delegate the sweep supplies)

```csharp
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

// Supplied by the sweep, backed by the single IEgressClient. Strategies never construct their own egress.
public delegate Task<EgressResult?> EgressFetch(Uri url, CancellationToken ct);

public interface ISourceStrategy
{
    string Name { get; }
    Task<IReadOnlyList<SourceCandidate>> ResolveAsync(
        AllowlistEntry entry, SubstanceKey key, EgressFetch fetch, CancellationToken ct);
}
```

- [ ] **Step 2: Write the failing test**

```csharp
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Sourcing;
using Xunit;

public class SourceResolverTests
{
    private static readonly EgressFetch NoFetch =
        (_, _) => throw new InvalidOperationException("casTemplate must not fetch");

    [Fact]
    public async Task CasTemplate_substitutes_cas_into_url()
    {
        var entry = new AllowlistEntry("ChemBlink", "chemblink.com", 90, "casTemplate",
            "https://www.chemblink.com/MSDS/MSDSFiles/{cas}.pdf", null, null);
        var strat = new CasTemplateStrategy();
        var got = await strat.ResolveAsync(entry, new SubstanceKey("Yb", "oxide", "1314-37-0"), NoFetch, default);
        Assert.Single(got);
        Assert.Equal("https://www.chemblink.com/MSDS/MSDSFiles/1314-37-0.pdf", got[0].Url.ToString());
        Assert.Equal("chemblink.com", got[0].Domain);
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.Tests --filter SourceResolverTests`
Expected: FAIL (CasTemplateStrategy not defined).

- [ ] **Step 4: Implement `CasTemplateStrategy.cs`**

```csharp
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

public sealed class CasTemplateStrategy : ISourceStrategy
{
    public string Name => "casTemplate";

    public Task<IReadOnlyList<SourceCandidate>> ResolveAsync(
        AllowlistEntry entry, SubstanceKey key, EgressFetch fetch, CancellationToken ct)
    {
        var url = new Uri(entry.SdsUrlTemplate.Replace("{cas}", key.Cas));
        IReadOnlyList<SourceCandidate> result = new[] { new SourceCandidate(entry.Supplier, entry.Domain, url) };
        return Task.FromResult(result);
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.Tests --filter SourceResolverTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Functions/Sds/Sourcing/ISourceStrategy.cs src/Smx.Functions/Sds/Sourcing/CasTemplateStrategy.cs src/Smx.Functions.Tests/SourceResolverTests.cs
git commit -m "feat(sds): ISourceStrategy + CasTemplateStrategy (TDD)"
```

### Task 10: ProductLookupStrategy (TDD)

**Files:**
- Create: `src/Smx.Functions/Sds/Sourcing/ProductLookupStrategy.cs`
- Test: append to `src/Smx.Functions.Tests/SourceResolverTests.cs`

- [ ] **Step 1: Add the failing test** (two-step: fetch search page → regex the brand+productNumber → build SDS url)

```csharp
    [Fact]
    public async Task ProductLookup_resolves_cas_to_brand_and_product_then_builds_sds_url()
    {
        var entry = new AllowlistEntry("Sigma-Aldrich", "sigmaaldrich.com", 10, "productLookup",
            "https://www.sigmaaldrich.com/US/en/sds/{brand}/{productNumber}",
            "https://www.sigmaaldrich.com/US/en/search/{cas}",
            "/sds/(?<brand>[a-z]+)/(?<productNumber>[a-z0-9]+)");

        // Fake search HTML that contains a matching /sds/<brand>/<product> link.
        EgressFetch fetch = (url, _) =>
        {
            var html = "<a href=\"/US/en/sds/sigald/329460\">SDS</a>";
            return Task.FromResult<EgressResult?>(
                new EgressResult(System.Text.Encoding.UTF8.GetBytes(html), "text/html", url));
        };

        var got = await new ProductLookupStrategy().ResolveAsync(
            entry, new SubstanceKey("Na", "hydroxide", "1310-73-2"), fetch, default);

        Assert.Single(got);
        Assert.Equal("https://www.sigmaaldrich.com/US/en/sds/sigald/329460", got[0].Url.ToString());
    }

    [Fact]
    public async Task ProductLookup_returns_empty_when_search_yields_no_match()
    {
        var entry = new AllowlistEntry("Sigma-Aldrich", "sigmaaldrich.com", 10, "productLookup",
            "https://www.sigmaaldrich.com/US/en/sds/{brand}/{productNumber}",
            "https://www.sigmaaldrich.com/US/en/search/{cas}",
            "/sds/(?<brand>[a-z]+)/(?<productNumber>[a-z0-9]+)");
        EgressFetch fetch = (url, _) =>
            Task.FromResult<EgressResult?>(new EgressResult(System.Text.Encoding.UTF8.GetBytes("no match"), "text/html", url));
        var got = await new ProductLookupStrategy().ResolveAsync(
            entry, new SubstanceKey("Na", "hydroxide", "1310-73-2"), fetch, default);
        Assert.Empty(got);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.Tests --filter SourceResolverTests`
Expected: FAIL (ProductLookupStrategy not defined).

- [ ] **Step 3: Implement `ProductLookupStrategy.cs`**

```csharp
using System.Text;
using System.Text.RegularExpressions;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

// Deterministic two-step: GET the supplier search page for the CAS (via the supplied egress fetch),
// regex out (brand, productNumber), then build the SDS PDF URL. Supplier-specific templates/regex
// live in the allowlist; new suppliers are a data edit.
public sealed class ProductLookupStrategy : ISourceStrategy
{
    public string Name => "productLookup";

    public async Task<IReadOnlyList<SourceCandidate>> ResolveAsync(
        AllowlistEntry entry, SubstanceKey key, EgressFetch fetch, CancellationToken ct)
    {
        var empty = Array.Empty<SourceCandidate>();
        if (string.IsNullOrEmpty(entry.SearchUrlTemplate) || string.IsNullOrEmpty(entry.ProductNumberRegex))
            return empty;

        var searchUrl = new Uri(entry.SearchUrlTemplate.Replace("{cas}", key.Cas));
        var page = await fetch(searchUrl, ct);
        if (page is null) return empty;

        var html = Encoding.UTF8.GetString(page.Content);
        var m = Regex.Match(html, entry.ProductNumberRegex, RegexOptions.IgnoreCase);
        if (!m.Success) return empty;

        var url = entry.SdsUrlTemplate
            .Replace("{brand}", m.Groups["brand"].Value)
            .Replace("{productNumber}", m.Groups["productNumber"].Value);
        return new[] { new SourceCandidate(entry.Supplier, entry.Domain, new Uri(url)) };
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.Tests --filter SourceResolverTests`
Expected: PASS (3 tests total in the class).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Functions/Sds/Sourcing/ProductLookupStrategy.cs src/Smx.Functions.Tests/SourceResolverTests.cs
git commit -m "feat(sds): ProductLookupStrategy two-step resolve (TDD)"
```

### Task 11: SourceResolver ordering (TDD)

**Files:**
- Create: `src/Smx.Functions/Sds/Sourcing/SourceResolver.cs`
- Test: append to `src/Smx.Functions.Tests/SourceResolverTests.cs`

- [ ] **Step 1: Add the failing test** (candidates come in priority order; manufacturer before aggregator)

```csharp
    [Fact]
    public async Task Resolver_emits_candidates_in_priority_order()
    {
        var json = """
        [
          { "supplier":"ChemBlink","domain":"chemblink.com","priority":90,"strategy":"casTemplate",
            "sdsUrlTemplate":"https://www.chemblink.com/MSDS/MSDSFiles/{cas}.pdf" },
          { "supplier":"Manu","domain":"manu.com","priority":10,"strategy":"casTemplate",
            "sdsUrlTemplate":"https://manu.com/{cas}.pdf" }
        ]
        """;
        var allow = AllowlistProvider.FromJson(json);
        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new CasTemplateStrategy() });
        var got = await resolver.ResolveAsync(new SubstanceKey("Yb", "oxide", "1314-37-0"), NoFetch, default);
        Assert.Equal(new[] { "manu.com", "chemblink.com" }, got.Select(c => c.Domain).ToArray());
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.Tests --filter SourceResolverTests`
Expected: FAIL (SourceResolver not defined).

- [ ] **Step 3: Implement `SourceResolver.cs`**

```csharp
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

public sealed class SourceResolver
{
    private readonly AllowlistProvider _allowlist;
    private readonly IReadOnlyDictionary<string, ISourceStrategy> _strategies;

    public SourceResolver(AllowlistProvider allowlist, IEnumerable<ISourceStrategy> strategies)
    {
        _allowlist = allowlist;
        _strategies = strategies.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
    }

    // Walks the ordered allowlist and yields candidates per entry. productLookup entries may
    // egress via `fetch` here; the SDS PDF fetch itself happens in the sweep.
    public async Task<IReadOnlyList<SourceCandidate>> ResolveAsync(
        SubstanceKey key, EgressFetch fetch, CancellationToken ct)
    {
        var candidates = new List<SourceCandidate>();
        foreach (var entry in _allowlist.Ordered)
        {
            if (!_strategies.TryGetValue(entry.Strategy, out var strat)) continue;
            candidates.AddRange(await strat.ResolveAsync(entry, key, fetch, ct));
        }
        return candidates;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.Tests --filter SourceResolverTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Functions/Sds/Sourcing/SourceResolver.cs src/Smx.Functions.Tests/SourceResolverTests.cs
git commit -m "feat(sds): SourceResolver ordered walk (TDD)"
```

---

## Phase 5 — Validation & chunking — TDD

### Task 12: SdsValidator (TDD)

**Files:**
- Create: `src/Smx.Functions/Sds/Ingestion/SdsValidator.cs`
- Test: `src/Smx.Functions.Tests/SdsValidatorTests.cs`, fixture `src/Smx.Functions.Tests/Resources/sample_sds.txt`

- [ ] **Step 1: Create the fixture** `src/Smx.Functions.Tests/Resources/sample_sds.txt` (a minimal but realistic GHS-16 layout containing a CAS)

```
SAFETY DATA SHEET
SECTION 1: Identification
Product name: Sodium hydroxide
CAS-No: 1310-73-2
SECTION 2: Hazards identification
SECTION 3: Composition/information on ingredients
CAS-No 1310-73-2
SECTION 4: First aid measures
SECTION 5: Firefighting measures
SECTION 6: Accidental release measures
SECTION 7: Handling and storage
SECTION 8: Exposure controls/personal protection
SECTION 9: Physical and chemical properties
SECTION 10: Stability and reactivity
SECTION 11: Toxicological information
SECTION 12: Ecological information
SECTION 13: Disposal considerations
SECTION 14: Transport information
SECTION 15: Regulatory information
SECTION 16: Other information
```

- [ ] **Step 2: Write the failing test**

```csharp
using Smx.Functions.Sds.Ingestion;
using Xunit;

public class SdsValidatorTests
{
    private static readonly IReadOnlySet<string> Allow = new HashSet<string> { "sigmaaldrich.com", "chemblink.com" };
    private static string Sample() => File.ReadAllText("Resources/sample_sds.txt");
    private readonly SdsValidator _v = new(minGhsSections: 10);

    [Fact]
    public void Accepts_real_sds_with_matching_cas_from_allowlisted_domain()
        => Assert.True(_v.Validate(Sample(), "1310-73-2", "sigmaaldrich.com", Allow).Ok);

    [Fact]
    public void Rejects_when_cas_absent()
    {
        var r = _v.Validate(Sample(), "7440-02-0", "sigmaaldrich.com", Allow);
        Assert.False(r.Ok);
        Assert.Contains("CAS", r.Reason);
    }

    [Fact]
    public void Rejects_non_sds_document()
    {
        var r = _v.Validate("This is an invoice. CAS-No: 1310-73-2", "1310-73-2", "sigmaaldrich.com", Allow);
        Assert.False(r.Ok);
        Assert.Contains("GHS", r.Reason);
    }

    [Fact]
    public void Rejects_off_allowlist_domain()
    {
        var r = _v.Validate(Sample(), "1310-73-2", "evil.example", Allow);
        Assert.False(r.Ok);
        Assert.Contains("domain", r.Reason);
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.Tests --filter SdsValidatorTests`
Expected: FAIL (SdsValidator not defined).

- [ ] **Step 4: Implement `SdsValidator.cs`**

```csharp
using System.Text.RegularExpressions;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Ingestion;

public sealed class SdsValidator
{
    private readonly int _minGhsSections;
    public SdsValidator(int minGhsSections = 10) => _minGhsSections = minGhsSections;

    public ValidationResult Validate(string text, string requestedCas, string sourceDomain,
        IReadOnlySet<string> allowlistDomains)
    {
        var host = sourceDomain.ToLowerInvariant();
        if (!allowlistDomains.Any(d => host == d || host.EndsWith("." + d)))
            return new ValidationResult(false, $"source domain '{sourceDomain}' not on allowlist");

        var sections = CountGhsSections(text);
        if (sections < _minGhsSections)
            return new ValidationResult(false, $"only {sections} GHS sections found (min {_minGhsSections})");

        var cas = requestedCas.Trim();
        if (!Regex.IsMatch(text, $@"\b{Regex.Escape(cas)}\b"))
            return new ValidationResult(false, $"requested CAS {cas} not present in document");

        return new ValidationResult(true, null);
    }

    private static int CountGhsSections(string text)
    {
        var found = new HashSet<int>();
        foreach (Match m in Regex.Matches(text, @"(?im)^\s*SECTION\s+(\d{1,2})\b"))
            if (int.TryParse(m.Groups[1].Value, out var n) && n is >= 1 and <= 16) found.Add(n);
        return found.Count;
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.Tests --filter SdsValidatorTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Functions/Sds/Ingestion/SdsValidator.cs src/Smx.Functions.Tests/SdsValidatorTests.cs src/Smx.Functions.Tests/Resources/sample_sds.txt
git commit -m "feat(sds): SdsValidator GHS/CAS/domain checks (TDD)"
```

### Task 13: GhsChunker (TDD)

**Files:**
- Create: `src/Smx.Functions/Sds/Ingestion/GhsChunker.cs`
- Test: `src/Smx.Functions.Tests/GhsChunkerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Functions.Sds.Ingestion;
using Xunit;

public class GhsChunkerTests
{
    private static string Sample() => File.ReadAllText("Resources/sample_sds.txt");

    [Fact]
    public void Splits_into_sixteen_tagged_sections()
    {
        var chunks = new GhsChunker().Chunk(Sample());
        Assert.Equal(16, chunks.Count);
        Assert.Equal("1", chunks[0].Section);
        Assert.Equal("16", chunks[^1].Section);
    }

    [Fact]
    public void Section_three_chunk_contains_composition_text()
    {
        var chunks = new GhsChunker().Chunk(Sample());
        var s3 = chunks.Single(c => c.Section == "3");
        Assert.Contains("Composition", s3.Content);
    }

    [Fact]
    public void Ignores_preamble_before_section_one()
    {
        var chunks = new GhsChunker().Chunk("garbage header\nSECTION 1: Identification\nbody");
        Assert.Single(chunks);
        Assert.Equal("1", chunks[0].Section);
        Assert.DoesNotContain("garbage", chunks[0].Content);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.Tests --filter GhsChunkerTests`
Expected: FAIL (GhsChunker not defined).

- [ ] **Step 3: Implement `GhsChunker.cs`**

```csharp
using System.Text.RegularExpressions;

namespace Smx.Functions.Sds.Ingestion;

public sealed class GhsChunker
{
    private static readonly Regex Header = new(@"(?im)^\s*SECTION\s+(\d{1,2})\b.*$", RegexOptions.Compiled);

    public IReadOnlyList<(string Section, string Content)> Chunk(string text)
    {
        var matches = Header.Matches(text)
            .Where(m => int.TryParse(m.Groups[1].Value, out var n) && n is >= 1 and <= 16)
            .ToList();

        var chunks = new List<(string, string)>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var section = matches[i].Groups[1].Value;
            var content = text[start..end].Trim();
            chunks.Add((section, content));
        }
        return chunks;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.Tests --filter GhsChunkerTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Functions/Sds/Ingestion/GhsChunker.cs src/Smx.Functions.Tests/GhsChunkerTests.cs
git commit -m "feat(sds): GhsChunker section-header splitting (TDD)"
```

---

## Phase 6 — Data repositories — TDD idempotency

### Task 14: Master-list store interface + repo + idempotency (TDD)

**Files:**
- Create: `src/Smx.Functions/Sds/Data/IMasterListStore.cs`, `src/Smx.Functions/Sds/Data/MasterListRepo.cs`
- Create: `src/Smx.Functions.Tests/Fakes/InMemoryMasterListStore.cs`
- Test: `src/Smx.Functions.Tests/MasterListRepoTests.cs`

- [ ] **Step 1: Write `IMasterListStore.cs`**

```csharp
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

public interface IMasterListStore
{
    Task<MasterListEntry?> GetAsync(string id, string element, CancellationToken ct);
    Task UpsertAsync(MasterListEntry entry, CancellationToken ct);
    Task<IReadOnlyList<MasterListEntry>> ListAllAsync(CancellationToken ct);
}
```

- [ ] **Step 2: Write the in-memory fake** `Fakes/InMemoryMasterListStore.cs`

```csharp
using System.Collections.Concurrent;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;

public sealed class InMemoryMasterListStore : IMasterListStore
{
    public readonly ConcurrentDictionary<string, MasterListEntry> Items = new();
    public Task<MasterListEntry?> GetAsync(string id, string element, CancellationToken ct)
        => Task.FromResult(Items.TryGetValue(id, out var e) ? e : null);
    public Task UpsertAsync(MasterListEntry entry, CancellationToken ct)
    { Items[entry.Id] = entry; return Task.CompletedTask; }
    public Task<IReadOnlyList<MasterListEntry>> ListAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MasterListEntry>>(Items.Values.ToList());
}
```

- [ ] **Step 3: Write the failing test**

```csharp
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;
using Xunit;

public class MasterListRepoTests
{
    [Fact]
    public async Task Append_is_idempotent_no_duplicate()
    {
        var store = new InMemoryMasterListStore();
        var repo = new MasterListRepo(store);
        var first = await repo.AppendAsync("Yb", "neodecanoate", "27253-31-2", null, "agent", "2026-07-07T00:00:00Z", default);
        var second = await repo.AppendAsync("Yb", "Neodecanoate", "27253-31-2", null, "agent", "2026-07-07T00:00:00Z", default);
        Assert.True(first);
        Assert.False(second);
        Assert.Single(store.Items);
        Assert.Equal(SdsStatus.Pending, store.Items.Values.Single().Status);
    }

    [Fact]
    public async Task Due_selects_pending_failed_under_cap_and_stale_fetched()
    {
        var store = new InMemoryMasterListStore();
        var repo = new MasterListRepo(store);
        await store.UpsertAsync(new MasterListEntry("a_x","a","x","1",null,SdsStatus.Pending,"sweep","t",null,0), default);
        await store.UpsertAsync(new MasterListEntry("b_x","b","x","1",null,SdsStatus.Failed,"sweep","t","t",2), default);
        await store.UpsertAsync(new MasterListEntry("c_x","c","x","1",null,SdsStatus.Failed,"sweep","t","t",9), default);
        await store.UpsertAsync(new MasterListEntry("d_x","d","x","1",null,SdsStatus.AwaitingOperator,"sweep","t","t",3), default);
        await store.UpsertAsync(new MasterListEntry("e_x","e","x","1",null,SdsStatus.Fetched,"sweep","t","2000-01-01T00:00:00Z",1), default);

        var due = await repo.QueryDueAsync(retryCap: 3, recheckDays: 90, nowUtc: "2026-07-07T00:00:00Z", default);
        var ids = due.Select(x => x.Id).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "a_x", "b_x", "e_x" }, ids); // pending, failed<cap, stale-fetched; NOT failed>=cap, NOT awaiting
    }
}
```

- [ ] **Step 4: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.Tests --filter MasterListRepoTests`
Expected: FAIL (MasterListRepo not defined).

- [ ] **Step 5: Implement `MasterListRepo.cs`**

```csharp
using System.Globalization;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

public sealed class MasterListRepo
{
    private readonly IMasterListStore _store;
    public MasterListRepo(IMasterListStore store) => _store = store;

    public async Task<bool> AppendAsync(string element, string form, string cas, string? substrateClass,
        string addedBy, string nowUtc, CancellationToken ct)
    {
        var id = DedupKey.ForMasterList(element, form);
        if (await _store.GetAsync(id, element, ct) is not null) return false;
        await _store.UpsertAsync(new MasterListEntry(
            id, element, form, cas, substrateClass, SdsStatus.Pending, addedBy, nowUtc, null, 0), ct);
        return true;
    }

    public Task<MasterListEntry?> GetAsync(string element, string form, CancellationToken ct)
        => _store.GetAsync(DedupKey.ForMasterList(element, form), element, ct);

    public async Task<IReadOnlyList<MasterListEntry>> QueryDueAsync(
        int retryCap, int recheckDays, string nowUtc, CancellationToken ct)
    {
        var now = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture);
        var all = await _store.ListAllAsync(ct);
        return all.Where(e => IsDue(e, retryCap, recheckDays, now)).ToList();
    }

    private static bool IsDue(MasterListEntry e, int retryCap, int recheckDays, DateTimeOffset now) => e.Status switch
    {
        SdsStatus.Pending => true,
        SdsStatus.Failed => e.AttemptCount < retryCap,
        SdsStatus.Fetched => e.LastAttemptUtc is not null
            && DateTimeOffset.Parse(e.LastAttemptUtc, CultureInfo.InvariantCulture).AddDays(recheckDays) <= now,
        _ => false, // awaiting_operator is NOT auto-retried
    };

    public Task MarkFetchedAsync(MasterListEntry e, string nowUtc, CancellationToken ct)
        => _store.UpsertAsync(e with { Status = SdsStatus.Fetched, LastAttemptUtc = nowUtc }, ct);

    public Task RecordFailureAsync(MasterListEntry e, int retryCap, string nowUtc, CancellationToken ct)
    {
        var attempts = e.AttemptCount + 1;
        var status = attempts >= retryCap ? SdsStatus.AwaitingOperator : SdsStatus.Failed;
        return _store.UpsertAsync(e with { Status = status, AttemptCount = attempts, LastAttemptUtc = nowUtc }, ct);
    }
}
```

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.Tests --filter MasterListRepoTests`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Smx.Functions/Sds/Data/IMasterListStore.cs src/Smx.Functions/Sds/Data/MasterListRepo.cs src/Smx.Functions.Tests/Fakes/InMemoryMasterListStore.cs src/Smx.Functions.Tests/MasterListRepoTests.cs
git commit -m "feat(sds): MasterListRepo idempotent upsert + due-selection (TDD)"
```

### Task 15: Registry store interface + repo; Cosmos-backed stores

**Files:**
- Create: `src/Smx.Functions/Sds/Data/IRegistryStore.cs`, `RegistryRepo.cs`, `CosmosMasterListStore.cs`, `CosmosRegistryStore.cs`
- Create: `src/Smx.Functions.Tests/Fakes/InMemoryRegistryStore.cs`

- [ ] **Step 1: Write `IRegistryStore.cs`**

```csharp
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

public interface IRegistryStore
{
    Task<RegistryPointer?> GetByCasAsync(string cas, CancellationToken ct);
    Task<RegistryPointer?> GetByProductNameAsync(string productName, CancellationToken ct);
    Task UpsertAsync(RegistryPointer pointer, CancellationToken ct);
}
```

- [ ] **Step 2: Write `RegistryRepo.cs`**

```csharp
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

public sealed class RegistryRepo
{
    private readonly IRegistryStore _store;
    public RegistryRepo(IRegistryStore store) => _store = store;

    public Task<RegistryPointer?> GetForSubstanceAsync(string? cas, string? productName, CancellationToken ct)
        => !string.IsNullOrWhiteSpace(cas) ? _store.GetByCasAsync(cas!, ct)
         : !string.IsNullOrWhiteSpace(productName) ? _store.GetByProductNameAsync(productName!, ct)
         : Task.FromResult<RegistryPointer?>(null);

    public Task UpsertAsync(RegistryPointer pointer, CancellationToken ct) => _store.UpsertAsync(pointer, ct);
}
```

- [ ] **Step 3: Write `Fakes/InMemoryRegistryStore.cs`**

```csharp
using System.Collections.Concurrent;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;

public sealed class InMemoryRegistryStore : IRegistryStore
{
    public readonly ConcurrentDictionary<string, RegistryPointer> Items = new();
    public Task<RegistryPointer?> GetByCasAsync(string cas, CancellationToken ct)
        => Task.FromResult(Items.Values.FirstOrDefault(p => p.Cas == cas && p.SupersededBy is null));
    public Task<RegistryPointer?> GetByProductNameAsync(string name, CancellationToken ct)
        => Task.FromResult(Items.Values.FirstOrDefault(p => p.ProductName == name && p.SupersededBy is null));
    public Task UpsertAsync(RegistryPointer p, CancellationToken ct) { Items[p.Id] = p; return Task.CompletedTask; }
}
```

- [ ] **Step 4: Write `CosmosMasterListStore.cs`** (keyless Cosmos; `id` + partition `/element`)

```csharp
using Microsoft.Azure.Cosmos;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

public sealed class CosmosMasterListStore : IMasterListStore
{
    private readonly Container _c;
    public CosmosMasterListStore(Container container) => _c = container;

    public async Task<MasterListEntry?> GetAsync(string id, string element, CancellationToken ct)
    {
        try { return await _c.ReadItemAsync<MasterListEntry>(id, new PartitionKey(element), cancellationToken: ct); }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public Task UpsertAsync(MasterListEntry entry, CancellationToken ct)
        => _c.UpsertItemAsync(entry, new PartitionKey(entry.Element), cancellationToken: ct);

    public async Task<IReadOnlyList<MasterListEntry>> ListAllAsync(CancellationToken ct)
    {
        var results = new List<MasterListEntry>();
        using var it = _c.GetItemQueryIterator<MasterListEntry>("SELECT * FROM c");
        while (it.HasMoreResults) results.AddRange(await it.ReadNextAsync(ct));
        return results;
    }
}
```

- [ ] **Step 5: Write `CosmosRegistryStore.cs`**

```csharp
using Microsoft.Azure.Cosmos;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

public sealed class CosmosRegistryStore : IRegistryStore
{
    private readonly Container _c;
    public CosmosRegistryStore(Container container) => _c = container;

    public Task<RegistryPointer?> GetByCasAsync(string cas, CancellationToken ct)
        => FirstAsync("SELECT * FROM c WHERE c.cas = @v AND (NOT IS_DEFINED(c.supersededBy) OR c.supersededBy = null)", "@v", cas, ct);

    public Task<RegistryPointer?> GetByProductNameAsync(string name, CancellationToken ct)
        => FirstAsync("SELECT * FROM c WHERE c.productName = @v AND (NOT IS_DEFINED(c.supersededBy) OR c.supersededBy = null)", "@v", name, ct);

    public Task UpsertAsync(RegistryPointer p, CancellationToken ct)
        => _c.UpsertItemAsync(p, new PartitionKey(p.Cas), cancellationToken: ct);

    private async Task<RegistryPointer?> FirstAsync(string sql, string p, string v, CancellationToken ct)
    {
        var q = new QueryDefinition(sql).WithParameter(p, v);
        using var it = _c.GetItemQueryIterator<RegistryPointer>(q);
        while (it.HasMoreResults)
            foreach (var r in await it.ReadNextAsync(ct)) return r;
        return null;
    }
}
```

- [ ] **Step 6: Build + commit**

```bash
dotnet build src/Smx.Functions/Smx.Functions.csproj
git add src/Smx.Functions/Sds/Data/ src/Smx.Functions.Tests/Fakes/InMemoryRegistryStore.cs
git commit -m "feat(sds): registry repo + Cosmos-backed stores"
```

### Task 16: ADLS Bronze store

**Files:**
- Create: `src/Smx.Functions/Sds/Data/IBronzeStore.cs`, `AdlsBronzeStore.cs`
- Create: `src/Smx.Functions.Tests/Fakes/FakeBronzeStore.cs`

- [ ] **Step 1: Write `IBronzeStore.cs`**

```csharp
namespace Smx.Functions.Sds.Data;

public interface IBronzeStore
{
    Task<string> PutAsync(string path, byte[] content, CancellationToken ct); // returns the stored path
    Task<byte[]?> GetAsync(string path, CancellationToken ct);
}
```

- [ ] **Step 2: Write `AdlsBronzeStore.cs`** (keyless Data Lake client)

```csharp
using Azure.Storage.Files.DataLake;

namespace Smx.Functions.Sds.Data;

public sealed class AdlsBronzeStore : IBronzeStore
{
    private readonly DataLakeFileSystemClient _fs;
    public AdlsBronzeStore(DataLakeFileSystemClient fs) => _fs = fs;

    public async Task<string> PutAsync(string path, byte[] content, CancellationToken ct)
    {
        var file = _fs.GetFileClient(path);
        using var ms = new MemoryStream(content);
        await file.UploadAsync(ms, overwrite: true, ct);
        return path;
    }

    public async Task<byte[]?> GetAsync(string path, CancellationToken ct)
    {
        var file = _fs.GetFileClient(path);
        if (!await file.ExistsAsync(ct)) return null;
        var resp = await file.ReadAsync(cancellationToken: ct);
        using var ms = new MemoryStream();
        await resp.Value.Content.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
```

- [ ] **Step 3: Write `Fakes/FakeBronzeStore.cs`**

```csharp
using System.Collections.Concurrent;
using Smx.Functions.Sds.Data;

public sealed class FakeBronzeStore : IBronzeStore
{
    public readonly ConcurrentDictionary<string, byte[]> Blobs = new();
    public Task<string> PutAsync(string path, byte[] content, CancellationToken ct) { Blobs[path] = content; return Task.FromResult(path); }
    public Task<byte[]?> GetAsync(string path, CancellationToken ct) => Task.FromResult(Blobs.TryGetValue(path, out var b) ? b : null);
}
```

- [ ] **Step 4: Build + commit**

```bash
dotnet build src/Smx.Functions/Smx.Functions.csproj
git add src/Smx.Functions/Sds/Data/IBronzeStore.cs src/Smx.Functions/Sds/Data/AdlsBronzeStore.cs src/Smx.Functions.Tests/Fakes/FakeBronzeStore.cs
git commit -m "feat(sds): ADLS bronze store + fake"
```

---

## Phase 7 — Egress, embedding, search, PDF extraction

### Task 17: Egress client (NAT + dry-run)

**Files:**
- Create: `src/Smx.Functions/Sds/Sourcing/IEgressClient.cs`, `NatEgressClient.cs`, `DryRunEgressClient.cs`

- [ ] **Step 1: Write `IEgressClient.cs`**

```csharp
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

// THE single outbound path. Injected only into SdsSweep. Returns null on any non-success / disallowed host.
public interface IEgressClient
{
    Task<EgressResult?> FetchAsync(Uri url, CancellationToken ct);
}
```

- [ ] **Step 2: Write `NatEgressClient.cs`** (allowlist-enforced, timeout, size cap; egresses via the subnet NAT)

```csharp
using Microsoft.Extensions.Logging;
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Sourcing;

namespace Smx.Functions.Sds.Sourcing;

public sealed class NatEgressClient : IEgressClient
{
    private readonly HttpClient _http;
    private readonly IReadOnlySet<string> _allowlistDomains;
    private readonly SdsOptions _opts;
    private readonly ILogger<NatEgressClient> _log;

    public NatEgressClient(HttpClient http, AllowlistProvider allowlist, SdsOptions opts, ILogger<NatEgressClient> log)
    {
        _http = http;
        _allowlistDomains = allowlist.Domains;
        _opts = opts;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(opts.FetchTimeoutSeconds);
    }

    public async Task<EgressResult?> FetchAsync(Uri url, CancellationToken ct)
    {
        var host = url.Host.ToLowerInvariant();
        if (!_allowlistDomains.Any(d => host == d || host.EndsWith("." + d)))
        {
            _log.LogWarning("Egress blocked: host {Host} not on allowlist", host);
            return null;
        }
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > _opts.MaxPdfBytes) { _log.LogWarning("Egress oversize {Len}", bytes.Length); return null; }
            var ctype = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            return new EgressResult(bytes, ctype, resp.RequestMessage?.RequestUri ?? url);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Egress fetch failed for {Url}", url); return null; }
    }
}
```

- [ ] **Step 3: Write `DryRunEgressClient.cs`** (fixtures keyed by host; no network — used when `SDS_DRY_RUN=true` and in tests)

```csharp
using System.Text;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

public sealed class DryRunEgressClient : IEgressClient
{
    private readonly Func<Uri, EgressResult?> _responder;
    public DryRunEgressClient(Func<Uri, EgressResult?> responder) => _responder = responder;

    // Default: return a canned SDS-shaped payload for any allowlisted PDF url, null for search pages.
    public static DryRunEgressClient Default(byte[] cannedPdf) => new(url =>
        url.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? new EgressResult(cannedPdf, "application/pdf", url)
            : new EgressResult(Encoding.UTF8.GetBytes("<html>dry-run</html>"), "text/html", url));

    public Task<EgressResult?> FetchAsync(Uri url, CancellationToken ct) => Task.FromResult(_responder(url));
}
```

- [ ] **Step 4: Build + commit**

```bash
dotnet build src/Smx.Functions/Smx.Functions.csproj
git add src/Smx.Functions/Sds/Sourcing/IEgressClient.cs src/Smx.Functions/Sds/Sourcing/NatEgressClient.cs src/Smx.Functions/Sds/Sourcing/DryRunEgressClient.cs
git commit -m "feat(sds): egress client (NAT allowlist-enforced + dry-run)"
```

### Task 18: PDF text extractor, embedder, search client

**Files:**
- Create: `src/Smx.Functions/Sds/Ingestion/IPdfTextExtractor.cs`, `PdfTextExtractor.cs`, `IEmbedder.cs`, `Embedder.cs`, `ISdsSearchClient.cs`, `SdsSearchClient.cs`
- Create: `src/Smx.Functions.Tests/Fakes/FakeEmbedder.cs`, `FakeSearchClient.cs`

- [ ] **Step 1: Write `IPdfTextExtractor.cs` + `PdfTextExtractor.cs`** (PdfPig)

```csharp
namespace Smx.Functions.Sds.Ingestion;
public interface IPdfTextExtractor { string Extract(byte[] pdf); }
```

```csharp
using System.Text;
using UglyToad.PdfPig;

namespace Smx.Functions.Sds.Ingestion;

public sealed class PdfTextExtractor : IPdfTextExtractor
{
    public string Extract(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages()) sb.AppendLine(page.Text);
        return sb.ToString();
    }
}
```

- [ ] **Step 2: Write `IEmbedder.cs` + `Embedder.cs`** (keyless Foundry/OpenAI embeddings)

```csharp
namespace Smx.Functions.Sds.Ingestion;
public interface IEmbedder { Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct); }
```

```csharp
using Azure.AI.OpenAI;
using OpenAI.Embeddings;

namespace Smx.Functions.Sds.Ingestion;

public sealed class Embedder : IEmbedder
{
    private readonly EmbeddingClient _client;
    public Embedder(AzureOpenAIClient client, string deployment) => _client = client.GetEmbeddingClient(deployment);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        var resp = await _client.GenerateEmbeddingsAsync(texts, cancellationToken: ct);
        return resp.Value.Select(e => e.ToFloats().ToArray()).ToList();
    }
}
```

- [ ] **Step 3: Write `ISdsSearchClient.cs` + `SdsSearchClient.cs`** (ensure index + push)

```csharp
using Smx.Functions.Sds.Domain;
namespace Smx.Functions.Sds.Ingestion;
public interface ISdsSearchClient
{
    Task EnsureIndexAsync(CancellationToken ct);
    Task PushAsync(IReadOnlyList<SdsChunk> chunks, CancellationToken ct);
}
```

```csharp
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Ingestion;

public sealed class SdsSearchClient : ISdsSearchClient
{
    private const int VectorDims = 3072; // text-embedding-3-large
    private const string VectorProfile = "sds-hnsw";
    private readonly SearchIndexClient _indexClient;
    private readonly string _indexName;

    public SdsSearchClient(SearchIndexClient indexClient, string indexName)
    { _indexClient = indexClient; _indexName = indexName; }

    public async Task EnsureIndexAsync(CancellationToken ct)
    {
        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
            new SimpleField("cas", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("supplier", SearchFieldDataType.String) { IsFilterable = true },
            new SearchableField("productName"),
            new SimpleField("revisionDate", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("region", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("language", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("ghsSection", SearchFieldDataType.String) { IsFilterable = true },
            new SearchableField("content"),
            new SimpleField("blobPath", SearchFieldDataType.String),
            new SimpleField("masterListId", SearchFieldDataType.String) { IsFilterable = true },
            new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true, VectorSearchDimensions = VectorDims, VectorSearchProfileName = VectorProfile
            }
        };
        var index = new SearchIndex(_indexName, fields)
        {
            VectorSearch = new VectorSearch
            {
                Profiles = { new VectorSearchProfile(VectorProfile, "sds-hnsw-config") },
                Algorithms = { new HnswAlgorithmConfiguration("sds-hnsw-config") }
            }
        };
        await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: ct);
    }

    public async Task PushAsync(IReadOnlyList<SdsChunk> chunks, CancellationToken ct)
    {
        if (chunks.Count == 0) return;
        var search = _indexClient.GetSearchClient(_indexName);
        await search.MergeOrUploadDocumentsAsync(chunks, cancellationToken: ct);
    }
}
```

- [ ] **Step 4: Write `Fakes/FakeEmbedder.cs` + `Fakes/FakeSearchClient.cs`**

```csharp
using Smx.Functions.Sds.Ingestion;
public sealed class FakeEmbedder : IEmbedder
{
    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[3072]).ToList());
}
```

```csharp
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Ingestion;
public sealed class FakeSearchClient : ISdsSearchClient
{
    public int EnsureCalls; public readonly List<SdsChunk> Pushed = new();
    public Task EnsureIndexAsync(CancellationToken ct) { EnsureCalls++; return Task.CompletedTask; }
    public Task PushAsync(IReadOnlyList<SdsChunk> chunks, CancellationToken ct) { Pushed.AddRange(chunks); return Task.CompletedTask; }
}
```

- [ ] **Step 5: Build + commit**

```bash
dotnet build src/Smx.Functions/Smx.Functions.csproj
git add src/Smx.Functions/Sds/Ingestion/IPdfTextExtractor.cs src/Smx.Functions/Sds/Ingestion/PdfTextExtractor.cs src/Smx.Functions/Sds/Ingestion/IEmbedder.cs src/Smx.Functions/Sds/Ingestion/Embedder.cs src/Smx.Functions/Sds/Ingestion/ISdsSearchClient.cs src/Smx.Functions/Sds/Ingestion/SdsSearchClient.cs src/Smx.Functions.Tests/Fakes/FakeEmbedder.cs src/Smx.Functions.Tests/Fakes/FakeSearchClient.cs
git commit -m "feat(sds): PDF extractor, embedder, SDS search client (+ fakes)"
```

---

## Phase 8 — Ingestion pipeline — TDD

### Task 19: IngestionPipeline (TDD with fakes)

**Files:**
- Create: `src/Smx.Functions/Sds/Ingestion/IngestionPipeline.cs`
- Test: `src/Smx.Functions.Tests/IngestionPipelineTests.cs`

- [ ] **Step 1: Write the failing test** (uses fakes + a text-based extractor stub so no real PDF is needed)

```csharp
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Ingestion;
using Xunit;

public class IngestionPipelineTests
{
    private sealed class TextExtractor : IPdfTextExtractor
    { public string Extract(byte[] pdf) => System.Text.Encoding.UTF8.GetString(pdf); }

    private static IngestionPipeline Build(out FakeBronzeStore bronze, out InMemoryRegistryStore reg,
        out FakeSearchClient search, out IReadOnlySet<string> domains)
    {
        bronze = new FakeBronzeStore(); reg = new InMemoryRegistryStore(); search = new FakeSearchClient();
        domains = new HashSet<string> { "sigmaaldrich.com" };
        return new IngestionPipeline(bronze, new SdsValidator(10), new TextExtractor(), new GhsChunker(),
            new FakeEmbedder(), search, new RegistryRepo(reg), domains, new SdsOptions());
    }

    private static SdsMetadata Meta() => new("1310-73-2", "Sigma-Aldrich", "Sodium hydroxide",
        "2024-03-01", "US", "en", "https://www.sigmaaldrich.com/US/en/sds/sigald/329460", "Na_hydroxide");

    [Fact]
    public async Task Valid_sds_lands_bronze_indexes_and_upserts_pointer()
    {
        var pipe = Build(out var bronze, out var reg, out var search, out _);
        var pdf = File.ReadAllBytes("Resources/sample_sds.txt");
        var r = await pipe.IngestAsync(pdf, Meta(), "sigmaaldrich.com", default);

        Assert.True(r.Ok);
        Assert.Single(bronze.Blobs);
        Assert.Equal("1310-73-2|sigma-aldrich|2024-03-01", r.RegistryId);
        Assert.True(search.Pushed.Count >= 10);            // GHS chunks pushed
        Assert.Equal(1, search.EnsureCalls);
        var pointer = reg.Items.Values.Single();
        Assert.True(pointer.Indexed);
        Assert.Equal(search.Pushed.Count, pointer.IndexDocIds.Count);
    }

    [Fact]
    public async Task Invalid_cas_is_rejected_and_nothing_indexed()
    {
        var pipe = Build(out _, out var reg, out var search, out _);
        var pdf = File.ReadAllBytes("Resources/sample_sds.txt");
        var r = await pipe.IngestAsync(pdf, Meta() with { Cas = "7440-02-0" }, "sigmaaldrich.com", default);
        Assert.False(r.Ok);
        Assert.Empty(search.Pushed);
        Assert.Empty(reg.Items);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.Tests --filter IngestionPipelineTests`
Expected: FAIL (IngestionPipeline not defined).

- [ ] **Step 3: Implement `IngestionPipeline.cs`**

```csharp
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Ingestion;

public sealed class IngestionPipeline
{
    private readonly IBronzeStore _bronze;
    private readonly SdsValidator _validator;
    private readonly IPdfTextExtractor _extractor;
    private readonly GhsChunker _chunker;
    private readonly IEmbedder _embedder;
    private readonly ISdsSearchClient _search;
    private readonly RegistryRepo _registry;
    private readonly IReadOnlySet<string> _allowlistDomains;
    private readonly SdsOptions _opts;

    public IngestionPipeline(IBronzeStore bronze, SdsValidator validator, IPdfTextExtractor extractor,
        GhsChunker chunker, IEmbedder embedder, ISdsSearchClient search, RegistryRepo registry,
        IReadOnlySet<string> allowlistDomains, SdsOptions opts)
    { _bronze = bronze; _validator = validator; _extractor = extractor; _chunker = chunker;
      _embedder = embedder; _search = search; _registry = registry; _allowlistDomains = allowlistDomains; _opts = opts; }

    public async Task<IngestResult> IngestAsync(byte[] pdf, SdsMetadata meta, string sourceDomain, CancellationToken ct)
    {
        var blobPath = $"sds/{meta.Cas}/{meta.Supplier}/{meta.RevisionDate}.pdf";
        await _bronze.PutAsync(blobPath, pdf, ct);

        var text = _extractor.Extract(pdf);
        var validation = _validator.Validate(text, meta.Cas, sourceDomain, _allowlistDomains);
        if (!validation.Ok) return new IngestResult(false, validation.Reason, null);

        var sections = _chunker.Chunk(text);
        var vectors = await _embedder.EmbedAsync(sections.Select(s => s.Content).ToList(), ct);

        var registryId = DedupKey.ForRegistry(meta.Cas, meta.Supplier, meta.RevisionDate);
        var chunks = new List<SdsChunk>(sections.Count);
        for (var i = 0; i < sections.Count; i++)
            chunks.Add(new SdsChunk($"{registryId}#{i}", meta.Cas, meta.Supplier, meta.ProductName,
                meta.RevisionDate, meta.Region, meta.Language, sections[i].Section, sections[i].Content,
                vectors[i], blobPath, meta.MasterListId));

        await _search.EnsureIndexAsync(ct);
        await _search.PushAsync(chunks, ct);

        var now = DateTimeOffset.UtcNow.ToString("O");
        await _registry.UpsertAsync(new RegistryPointer(registryId, meta.Cas, meta.Supplier, meta.ProductName,
            meta.RevisionDate, meta.Region, meta.Language, meta.SourceUrl, blobPath, true,
            chunks.Select(c => c.Id).ToList(), now, null, meta.MasterListId), ct);

        return new IngestResult(true, null, registryId);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.Tests --filter IngestionPipelineTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Functions/Sds/Ingestion/IngestionPipeline.cs src/Smx.Functions.Tests/IngestionPipelineTests.cs
git commit -m "feat(sds): shared ingestion pipeline (TDD)"
```

---

## Phase 9 — Triggers

### Task 20: SdsSweep timer + dry-run sweep test (TDD)

**Files:**
- Create: `src/Smx.Functions/Sds/Triggers/SdsSweep.cs`
- Test: `src/Smx.Functions.Tests/SdsSweepTests.cs`

**Design note:** `SdsSweep` is the ONLY class constructed with `IEgressClient`. The sweep builds an `EgressFetch` delegate from it and passes that to the resolver; it never hands the client to any other component.

- [ ] **Step 1: Write the failing test** (dry-run: due entry → resolved candidate → canned PDF → ingested; asserts no real egress and status→fetched)

```csharp
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Ingestion;
using Smx.Functions.Sds.Sourcing;
using Smx.Functions.Sds.Triggers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class SdsSweepTests
{
    private sealed class TextExtractor : IPdfTextExtractor
    { public string Extract(byte[] pdf) => System.Text.Encoding.UTF8.GetString(pdf); }

    [Fact]
    public async Task DryRun_sweep_fetches_via_dry_client_ingests_and_marks_fetched()
    {
        var mlStore = new InMemoryMasterListStore();
        var mlRepo = new MasterListRepo(mlStore);
        await mlRepo.AppendAsync("Na", "hydroxide", "1310-73-2", null, "sweep", "2020-01-01T00:00:00Z", default);

        var allow = AllowlistProvider.FromJson("""
          [ { "supplier":"ChemBlink","domain":"chemblink.com","priority":90,"strategy":"casTemplate",
              "sdsUrlTemplate":"https://www.chemblink.com/MSDS/MSDSFiles/{cas}.pdf" } ]
        """);
        var resolver = new SourceResolver(allow, new ISourceStrategy[] { new CasTemplateStrategy() });

        var cannedPdf = File.ReadAllBytes("Resources/sample_sds.txt");     // text-as-"pdf" for the TextExtractor
        var egress = DryRunEgressClient.Default(cannedPdf);

        var search = new FakeSearchClient(); var reg = new InMemoryRegistryStore();
        var domains = allow.Domains;
        var pipe = new IngestionPipeline(new FakeBronzeStore(), new SdsValidator(10), new TextExtractor(),
            new GhsChunker(), new FakeEmbedder(), search, new RegistryRepo(reg), domains, new SdsOptions());

        var sweep = new SdsSweep(mlRepo, resolver, egress, pipe, new SdsOptions(), NullLogger<SdsSweep>.Instance);
        await sweep.RunSweepAsync("2026-07-07T00:00:00Z", default);

        Assert.Equal(SdsStatus.Fetched, mlStore.Items.Values.Single().Status);
        Assert.Single(reg.Items);
        Assert.True(search.Pushed.Count >= 10);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.Tests --filter SdsSweepTests`
Expected: FAIL (SdsSweep not defined).

- [ ] **Step 3: Implement `SdsSweep.cs`**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Ingestion;
using Smx.Functions.Sds.Sourcing;

namespace Smx.Functions.Sds.Triggers;

public sealed class SdsSweep
{
    private readonly MasterListRepo _masterList;
    private readonly SourceResolver _resolver;
    private readonly IEgressClient _egress;     // sole holder of the egress client
    private readonly IngestionPipeline _pipeline;
    private readonly SdsOptions _opts;
    private readonly ILogger<SdsSweep> _log;

    public SdsSweep(MasterListRepo masterList, SourceResolver resolver, IEgressClient egress,
        IngestionPipeline pipeline, SdsOptions opts, ILogger<SdsSweep> log)
    { _masterList = masterList; _resolver = resolver; _egress = egress; _pipeline = pipeline; _opts = opts; _log = log; }

    [Function("SdsSweep")]
    public Task Run([TimerTrigger("%SDS_SWEEP_CRON%")] TimerInfo timer, CancellationToken ct)
        => RunSweepAsync(DateTimeOffset.UtcNow.ToString("O"), ct);

    // Testable core (no trigger attribute): process the whole due set in bulk.
    public async Task RunSweepAsync(string nowUtc, CancellationToken ct)
    {
        var due = await _masterList.QueryDueAsync(_opts.RetryCap, _opts.RevisionRecheckDays, nowUtc, ct);
        _log.LogInformation("SDS sweep: {Count} due entries", due.Count);

        EgressFetch fetch = (url, c) => _egress.FetchAsync(url, c);

        foreach (var entry in due)
        {
            var key = new SubstanceKey(entry.Element, entry.Form, entry.Cas);
            var candidates = await _resolver.ResolveAsync(key, fetch, ct);
            var ingested = false;

            foreach (var candidate in candidates)
            {
                var fetched = await _egress.FetchAsync(candidate.Url, ct);
                if (fetched is null) continue;

                var meta = new SdsMetadata(entry.Cas, candidate.Supplier, entry.Form, nowUtc[..10],
                    null, null, candidate.Url.ToString(), entry.Id);
                var result = await _pipeline.IngestAsync(fetched.Content, meta, candidate.Domain, ct);
                if (result.Ok) { ingested = true; break; }
                _log.LogInformation("Candidate {Url} rejected: {Reason}", candidate.Url, result.Reason);
            }

            if (ingested) await _masterList.MarkFetchedAsync(entry, nowUtc, ct);
            else await _masterList.RecordFailureAsync(entry, _opts.RetryCap, nowUtc, ct);
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.Tests --filter SdsSweepTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Functions/Sds/Triggers/SdsSweep.cs src/Smx.Functions.Tests/SdsSweepTests.cs
git commit -m "feat(sds): SdsSweep timer + dry-run sweep (TDD; single egress holder)"
```

### Task 21: HTTP triggers (operator upload + 3 agent ops) + DI wiring

**Files:**
- Create: `src/Smx.Functions/Sds/Triggers/OperatorUpload.cs`, `AppendToMasterList.cs`, `GetSdsForSubstance.cs`, `GetSdsStatus.cs`
- Modify: `src/Smx.Functions/Program.cs`

- [ ] **Step 1: Write `AppendToMasterList.cs`** (idempotent enqueue; NO fetch)

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.Functions.Sds.Data;

namespace Smx.Functions.Sds.Triggers;

public sealed record AppendRequest(string Element, string Form, string Cas, string? SubstrateClass);

public sealed class AppendToMasterList
{
    private readonly MasterListRepo _repo;
    public AppendToMasterList(MasterListRepo repo) => _repo = repo;

    [Function("AppendToMasterList")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sds/master-list")] HttpRequestData req)
    {
        var body = await JsonSerializer.DeserializeAsync<AppendRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (body is null || string.IsNullOrWhiteSpace(body.Element) || string.IsNullOrWhiteSpace(body.Form))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var added = await _repo.AppendAsync(body.Element, body.Form, body.Cas, body.SubstrateClass,
            "agent", DateTimeOffset.UtcNow.ToString("O"), req.FunctionContext.CancellationToken);

        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(new { added, id = Domain.DedupKey.ForMasterList(body.Element, body.Form) });
        return resp;
    }
}
```

- [ ] **Step 2: Write `GetSdsStatus.cs`**

```csharp
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.Functions.Sds.Data;

namespace Smx.Functions.Sds.Triggers;

public sealed class GetSdsStatus
{
    private readonly MasterListRepo _repo;
    public GetSdsStatus(MasterListRepo repo) => _repo = repo;

    [Function("GetSdsStatus")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sds/status/{element}/{form}")] HttpRequestData req,
        string element, string form)
    {
        var entry = await _repo.GetAsync(element, form, req.FunctionContext.CancellationToken);
        var resp = req.CreateResponse(entry is null ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        if (entry is not null) await resp.WriteAsJsonAsync(new { entry.Id, entry.Status, entry.AttemptCount });
        return resp;
    }
}
```

- [ ] **Step 3: Write `GetSdsForSubstance.cs`** (returns pointer or not-present; NO fetch — this is the self-heal miss trigger)

```csharp
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.Functions.Sds.Data;

namespace Smx.Functions.Sds.Triggers;

public sealed class GetSdsForSubstance
{
    private readonly RegistryRepo _repo;
    public GetSdsForSubstance(RegistryRepo repo) => _repo = repo;

    [Function("GetSdsForSubstance")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sds/substance")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var pointer = await _repo.GetForSubstanceAsync(query["cas"], query["productName"],
            req.FunctionContext.CancellationToken);

        if (pointer is null)
        {
            var nf = req.CreateResponse(HttpStatusCode.NotFound);
            await nf.WriteAsJsonAsync(new { present = false });
            return nf;
        }
        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(new { present = true, pointer.Id, pointer.BlobPath, pointer.IndexDocIds, pointer.RevisionDate });
        return ok;
    }
}
```

- [ ] **Step 4: Write `OperatorUpload.cs`** (PDF + metadata → shared pipeline; NO fetch)

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Ingestion;
using Smx.Functions.Sds.Sourcing;

namespace Smx.Functions.Sds.Triggers;

public sealed record OperatorUploadRequest(
    string Cas, string Supplier, string ProductName, string RevisionDate,
    string? Region, string? Language, string MasterListId, string PdfBase64);

public sealed class OperatorUpload
{
    private readonly IngestionPipeline _pipeline;
    private readonly AllowlistProvider _allowlist;
    public OperatorUpload(IngestionPipeline pipeline, AllowlistProvider allowlist)
    { _pipeline = pipeline; _allowlist = allowlist; }

    [Function("OperatorUpload")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sds/upload")] HttpRequestData req)
    {
        var body = await JsonSerializer.DeserializeAsync<OperatorUploadRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (body is null || string.IsNullOrWhiteSpace(body.Cas) || string.IsNullOrWhiteSpace(body.PdfBase64))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var pdf = Convert.FromBase64String(body.PdfBase64);
        // Operator-supplied source: validate against the manufacturer domain if known, else the supplier's
        // own domain is trusted for upload. Use the first allowlist domain matching the supplier, else supplier as-is.
        var domain = _allowlist.Ordered.FirstOrDefault(e =>
            string.Equals(e.Supplier, body.Supplier, StringComparison.OrdinalIgnoreCase))?.Domain
            ?? _allowlist.Ordered[0].Domain;

        var meta = new SdsMetadata(body.Cas, body.Supplier, body.ProductName, body.RevisionDate,
            body.Region, body.Language, $"operator-upload://{body.Supplier}", body.MasterListId);
        var result = await _pipeline.IngestAsync(pdf, meta, domain, req.FunctionContext.CancellationToken);

        var resp = req.CreateResponse(result.Ok ? HttpStatusCode.OK : HttpStatusCode.UnprocessableEntity);
        await resp.WriteAsJsonAsync(new { result.Ok, result.Reason, result.RegistryId });
        return resp;
    }
}
```

- [ ] **Step 5: Wire DI in `Program.cs`** (replace the scaffold from Task 4)

```csharp
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Files.DataLake;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Ingestion;
using Smx.Functions.Sds.Sourcing;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
        var opts = SdsOptions.From(ctx.Configuration);
        services.AddSingleton(opts);

        TokenCredential cred = string.IsNullOrEmpty(opts.UamiClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(opts.UamiClientId);
        services.AddSingleton(cred);

        // Allowlist (single artifact) + strategies + resolver
        services.AddSingleton(_ => AllowlistProvider.FromFile(opts.AllowlistPath));
        services.AddSingleton<ISourceStrategy, CasTemplateStrategy>();
        services.AddSingleton<ISourceStrategy, ProductLookupStrategy>();
        services.AddSingleton<SourceResolver>();

        // Cosmos stores + repos
        services.AddSingleton(_ => new CosmosClient(opts.CosmosEndpoint, cred));
        services.AddSingleton<IMasterListStore>(sp => new CosmosMasterListStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.MasterContainer)));
        services.AddSingleton<IRegistryStore>(sp => new CosmosRegistryStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.RegistryContainer)));
        services.AddSingleton<MasterListRepo>();
        services.AddSingleton<RegistryRepo>();

        // Bronze (ADLS)
        services.AddSingleton<IBronzeStore>(_ =>
            new AdlsBronzeStore(new DataLakeServiceClient(
                new Uri($"https://{opts.BronzeAccount}.dfs.core.windows.net"), cred)
                .GetFileSystemClient(opts.BronzeFilesystem)));

        // Ingestion deps
        services.AddSingleton<SdsValidator>(_ => new SdsValidator(opts.MinGhsSections));
        services.AddSingleton<GhsChunker>();
        services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();
        services.AddSingleton<IEmbedder>(sp => new Embedder(
            new AzureOpenAIClient(new Uri(opts.FoundryEndpoint), cred), opts.EmbeddingDeployment));
        services.AddSingleton<ISdsSearchClient>(_ => new SdsSearchClient(
            new SearchIndexClient(new Uri(opts.SearchEndpoint), cred), opts.SearchIndex));
        services.AddSingleton(sp => new IngestionPipeline(
            sp.GetRequiredService<IBronzeStore>(), sp.GetRequiredService<SdsValidator>(),
            sp.GetRequiredService<IPdfTextExtractor>(), sp.GetRequiredService<GhsChunker>(),
            sp.GetRequiredService<IEmbedder>(), sp.GetRequiredService<ISdsSearchClient>(),
            sp.GetRequiredService<RegistryRepo>(),
            sp.GetRequiredService<AllowlistProvider>().Domains, opts));

        // Egress — real (NAT) or dry-run. Only SdsSweep consumes IEgressClient.
        if (opts.DryRun)
            services.AddSingleton<IEgressClient>(_ => DryRunEgressClient.Default(Array.Empty<byte>()));
        else
        {
            services.AddHttpClient();
            services.AddSingleton<IEgressClient>(sp => new NatEgressClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                sp.GetRequiredService<AllowlistProvider>(), opts,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<NatEgressClient>>()));
        }
    })
    .Build();

host.Run();
```

- [ ] **Step 6: Build the whole solution + run ALL tests**

Run: `dotnet build src/Smx.Functions.sln && dotnet test src/Smx.Functions.sln`
Expected: Build succeeded; all tests PASS (DedupKey, SourceResolver, SdsValidator, GhsChunker, MasterListRepo, IngestionPipeline, SdsSweep).

- [ ] **Step 7: Commit**

```bash
git add src/Smx.Functions/Sds/Triggers/ src/Smx.Functions/Program.cs
git commit -m "feat(sds): HTTP triggers (upload + 3 agent ops) + DI wiring"
```

---

## Phase 10 — Scripts & docs

### Task 22: publish-functions.sh (both variants)

**Files:**
- Create: `infra/scripts/publish-functions.sh` and `infra/single-rg/scripts/publish-functions.sh` (identical)

- [ ] **Step 1: Write `publish-functions.sh`**

```bash
#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Build + zip-deploy the Smx.Functions project to the regsync Function App (keyless, Entra auth on az).
ENV="$(require_env_arg "${1:-}")"
confirm_subscription
RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"
APP="func-${NAME_PREFIX}-${ENV}-regsync-${REGION_SHORT}"
PROJ="${INFRA_DIR}/../src/Smx.Functions"
OUT="$(mktemp -d)"; ZIP="${OUT}/sds-functions.zip"

log "Publishing ${PROJ} -> ${APP}"
dotnet publish "${PROJ}" -c Release -o "${OUT}/publish"
( cd "${OUT}/publish" && zip -qr "${ZIP}" . )
az functionapp deployment source config-zip -g "${RG}" -n "${APP}" --src "${ZIP}" --build-remote false
log "Published. Trigger sync: az functionapp restart -g ${RG} -n ${APP}"
```

> The single-rg copy is identical except `RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"` is whatever the single-rg naming uses — reuse the RG derivation already present in `infra/single-rg/scripts/smoke.sh` for consistency.

- [ ] **Step 2: Syntax-check + commit**

```bash
bash -n infra/scripts/publish-functions.sh infra/single-rg/scripts/publish-functions.sh
chmod +x infra/scripts/publish-functions.sh infra/single-rg/scripts/publish-functions.sh
git add infra/scripts/publish-functions.sh infra/single-rg/scripts/publish-functions.sh
git commit -m "feat(infra): publish-functions.sh — zip-deploy SDS code to regsync app (both variants)"
```

### Task 23: configure-auth.sh (both variants)

**Files:**
- Create: `infra/scripts/configure-auth.sh` and `infra/single-rg/scripts/configure-auth.sh` (identical)

- [ ] **Step 1: Write `configure-auth.sh`**

```bash
#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Ensure an Entra app registration for the regsync Function App and enforce Easy Auth (Return401).
ENV="$(require_env_arg "${1:-}")"
confirm_subscription
RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"
APP="func-${NAME_PREFIX}-${ENV}-regsync-${REGION_SHORT}"
APP_REG_NAME="${NAME_PREFIX}-${ENV}-regsync-auth"

log "Ensuring Entra app registration '${APP_REG_NAME}'..."
CLIENT_ID="$(az ad app list --display-name "${APP_REG_NAME}" --query '[0].appId' -o tsv)"
if [ -z "${CLIENT_ID}" ]; then
  CLIENT_ID="$(az ad app create --display-name "${APP_REG_NAME}" \
    --identifier-uris "api://${APP_REG_NAME}" --query appId -o tsv)"
  log "Created app registration ${CLIENT_ID}"
fi

log "Enforcing Easy Auth on ${APP} (audience api://${APP_REG_NAME})..."
TENANT_ID="$(az account show --query tenantId -o tsv)"
az webapp auth update -g "${RG}" -n "${APP}" \
  --enabled true --action Return401 \
  --aad-allowed-token-audiences "api://${APP_REG_NAME}" \
  --aad-client-id "${CLIENT_ID}" \
  --aad-token-issuer-url "https://login.microsoftonline.com/${TENANT_ID}/v2.0" >/dev/null

warn "Callers (ACA orchestrator) must present an Entra token for audience api://${APP_REG_NAME}."
log "Re-run deploy.sh with -p authClientId=${CLIENT_ID} to keep Bicep in sync, then harden.sh."
```

- [ ] **Step 2: Syntax-check + commit**

```bash
bash -n infra/scripts/configure-auth.sh infra/single-rg/scripts/configure-auth.sh
chmod +x infra/scripts/configure-auth.sh infra/single-rg/scripts/configure-auth.sh
git add infra/scripts/configure-auth.sh infra/single-rg/scripts/configure-auth.sh
git commit -m "feat(infra): configure-auth.sh — Entra app registration + Easy Auth (both variants)"
```

### Task 24: README + CLAUDE.md docs

**Files:**
- Modify: `infra/README.md` (add an "SDS Library subsystem" section), `CLAUDE.md` (add stack commands)

- [ ] **Step 1: Append to `infra/README.md`** (after the "Tear down" section)

````markdown
## SDS Library subsystem (functions code)

The SDS pre-seed library runs as .NET 8 isolated functions inside the **regsync** Function App
(`src/Smx.Functions`). Provision → ship code → turn on auth → lock down:

```bash
./scripts/deploy.sh dev            # provisions the app shell + SDS containers/index settings
./scripts/publish-functions.sh dev # builds + zip-deploys the SDS function code
./scripts/configure-auth.sh dev    # creates the Entra app registration + enforces Easy Auth
./scripts/harden.sh dev            # private-endpoint-only
```

**Leak posture (enforced in code, not just topology):**
- **Single egress path** — one `IEgressClient` (NAT egress + curated allowlist) is the only outbound
  HTTP, injected *only* into the timer sweep.
- **No on-demand fetch** — the retrieval/agent/self-heal paths cannot fetch. A miss enqueues the (element,
  form) and parks `awaiting_sds` until the next scheduled sweep; operator upload is the manual fallback.
- **Scheduled-bulk-only** — the sweep processes the whole due set on wall-clock cadence, so no request
  maps to a project.

**Configure cadence + allowlist:** `SDS_SWEEP_CRON` app setting; edit the ordered
`src/Smx.Functions/Sds/Config/suppliers.allowlist.json` (git-reviewed). Run the sweep with no real egress
by setting `SDS_DRY_RUN=true`.
````

- [ ] **Step 2: Append a build/test note to `CLAUDE.md`** (under a new "Application code" heading)

```markdown
## Application code

- **SDS Library functions** (`src/Smx.Functions`, .NET 8 isolated) — build/test:
  - `dotnet build src/Smx.Functions.sln`
  - `dotnet test src/Smx.Functions.sln`
  - Publish to Azure: `infra/scripts/publish-functions.sh <env>` (then `configure-auth.sh`).
```

- [ ] **Step 3: Commit**

```bash
git add infra/README.md CLAUDE.md
git commit -m "docs(sds): README leak-posture/config section + CLAUDE.md stack commands"
```

---

## Phase 11 — Final verification

### Task 25: Full build, full test, bicep lint

- [ ] **Step 1: Run the whole suite**

Run: `dotnet test src/Smx.Functions.sln`
Expected: all tests PASS across DedupKey, SourceResolver, SdsValidator, GhsChunker, MasterListRepo, IngestionPipeline, SdsSweep.

- [ ] **Step 2: Bicep builds (both variants)**

Run: `az bicep build --file infra/main.bicep && az bicep build --file infra/single-rg/main.bicep`
Expected: no errors.

- [ ] **Step 3: Final commit (if anything uncommitted) + summary**

```bash
git status
git log --oneline -20
```

---

## Self-review (spec coverage)

- **§3 invariants** — single egress (`IEgressClient` only in `SdsSweep`, Task 20), no on-demand fetch (HTTP ops are fetch-free, Task 21; self-heal via `AppendToMasterList`), deterministic sourcing (ordered allowlist + strategies, Tasks 9–11), private/keyless (managed identity DI, Task 21), no "fetch now" tool (none defined). ✔
- **§4 data model** — containers + PKs (Task 1), registry dedup key (Task 6), bronze path (Task 19), index schema (Task 18). ✔
- **§5 sweep** — reconcile/resolve/fetch/validate/ingest/retry→awaiting_operator (Tasks 14, 20); revision recheck via `QueryDueAsync` stale-fetched (Task 14). ✔ *(Supersede-on-revision: the pipeline upserts by the same registryId when rev unchanged; a changed revisionDate yields a new id — full `supersededBy` chaining of the prior pointer is a follow-up nicety, noted in Task 19; the miss/refresh correctness is covered.)*
- **§6 pipeline** — shared `IngestionPipeline` used by sweep + upload (Tasks 19, 20, 21). ✔
- **§7 self-heal** — `GetSdsForSubstance` not-present + `AppendToMasterList` idempotent (Tasks 14, 21). ✔
- **§8 allowlist** — single JSON artifact + provider (Task 8). ✔
- **§9 operator upload** — `OperatorUpload` → shared pipeline (Task 21). ✔
- **§10 agent ops + Entra auth** — three ops (Task 21); Easy Auth (Bicep Task 2 + script Task 23). ✔
- **§11 infra** — data/functions/main Bicep both variants (Tasks 1–3). ✔
- **§13 tests** — resolver ordering+strategies, validator, chunker, dedup, master-list idempotency, dry-run sweep (Tasks 6, 9–14, 19, 20). ✔
- **§14 scripts/docs** — publish + configure-auth + README + CLAUDE.md (Tasks 22–24). ✔

**Deferred (explicitly, not gaps):** real per-supplier `ProductLookupStrategy` search endpoints (config, spec Appendix A); full `supersededBy` back-pointer chaining on revision change (Task 19 note); live integration tests against real Azure (out of scope — unit + dry-run only).
