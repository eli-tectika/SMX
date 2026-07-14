using System.Text.Json;
using Smx.SearchProxy.Contracts;
using Smx.SearchProxy.Triggers;
using Xunit;

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

    // Every reason the pipeline can return must have a message written FOR THE MODEL — it is relayed verbatim
    // into the tool result. A reason with no case here falls to the generic "could not be completed", which
    // tells the agent nothing it can act on.
    [Theory]
    [InlineData("query_empty")]
    [InlineData("query_too_long")]
    [InlineData("unknown_intent")]
    [InlineData("max_results_out_of_range")]
    [InlineData("contains_guid")]
    [InlineData("contains_email")]
    [InlineData("contains_url")]
    [InlineData("contains_digit_run")]
    [InlineData("quota_exceeded")]
    [InlineData("quota_unavailable")]
    [InlineData("provider_failed")]
    [InlineData("provider_not_configured")]
    [InlineData("malformed_or_unknown_field")]
    [InlineData("empty_body")]
    public void EveryReason_HasAnInstructiveMessage(string reason)
    {
        var message = SearchHttp.Explain(reason);
        Assert.NotEqual("The external search could not be completed.", message);
        Assert.NotEmpty(message);
    }
}
