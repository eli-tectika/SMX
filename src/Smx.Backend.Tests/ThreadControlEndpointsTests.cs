using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Backend.Agents;
using Smx.Backend.Knowledge;
using Smx.Backend.Pipeline;
using Smx.Backend.Tests.Fakes;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// §7.3's control surface, over HTTP: the message door, cancel, and rerun. Driven through the real pipe
/// because the status codes ARE the contract — the web track codes against them verbatim, and a 409 where
/// the client expects a 422 is a failure it can only discover in front of an operator.
public class ThreadControlEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly InMemoryRunStore _runs = new();
    private readonly FakeAgentRuns _agents = new();
    private readonly PipelineSupervisor _supervisor;
    private readonly HttpClient _client;

    public ThreadControlEndpointsTests(WebApplicationFactory<Program> factory)
    {
        var runner = new PipelineRunner(_store, _runs, _agents, new ThreadEventHub(),
            new LearnedConclusionWriter(new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(),
                new FakeEmbedder(), NullLogger<LearnedConclusionWriter>.Instance), 2);
        _supervisor = new PipelineSupervisor(_store, _runs, runner, NullLogger<PipelineSupervisor>.Instance);
        _client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IRecordStore>(_store);
            s.AddSingleton<IRunStore>(_runs);
            s.AddSingleton(new ThreadEventHub());
            s.AddSingleton(_supervisor);
        })).CreateClient();
    }

    private async Task<ProjectDoc> SeedProjectAsync(string pid = "p1")
    {
        var doc = ProjectDoc.Create(pid, "Acme", "P", JsonDocument.Parse("{}").RootElement);
        await _store.UpsertProjectAsync(doc);
        return doc;
    }

    private static string At(int second) =>
        new DateTimeOffset(2026, 7, 27, 10, 0, second, TimeSpan.Zero).ToString("O");

    /// Holds the pipeline inside intake on the run's OWN token, so the test can drive a genuinely live run —
    /// and so an operator cancel is observed rather than ignored by a fake that never looks at its token.
    private TaskCompletionSource BlockIntakeOnItsToken()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _agents.Intake = async _ =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, _agents.LastIntakeToken);
            return AgentRunResult<ConstraintsDoc>.NeedsReview("unreachable");
        };
        return reached;
    }

    private Task<HttpResponseMessage> PostMessage(string pid, string stage, string text) =>
        _client.PostAsJsonAsync($"/projects/{pid}/stages/{stage}/messages", new { text });

    // ---- POST …/messages ----------------------------------------------------------------------------

    [Fact]
    public async Task Message_is_accepted_and_lands_on_the_thread()
    {
        await SeedProjectAsync();

        var res = await PostMessage("p1", Stages.Discovery, "why did you drop the Zr neodecanoate?");

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("messageId").GetString()));
        // seq is the client's dedupe key against the thread it already holds, so it must be the position in
        // THAT thread — not a counter of messages.
        Assert.Equal(1, body.GetProperty("seq").GetInt32());
        Assert.False(body.GetProperty("queued").GetBoolean());

        var thread = await _client.GetFromJsonAsync<JsonElement[]>("/projects/p1/stages/discovery/thread");
        var entry = Assert.Single(thread!);
        Assert.Equal("message", entry.GetProperty("kind").GetString());
        Assert.Equal("operator", entry.GetProperty("role").GetString());
        Assert.Equal("queued", entry.GetProperty("status").GetString());
        Assert.Equal(1, entry.GetProperty("seq").GetInt32());
    }

    /// The seq handed back must be the position in the MERGED thread — runs included — or the client files
    /// the new message above entries that came before it.
    [Fact]
    public async Task Message_seq_counts_the_runs_already_on_the_thread()
    {
        await SeedProjectAsync();
        await _runs.UpsertAsync(new RunDoc
        {
            Id = RunIds.Run("p1", Stages.Discovery, 1), ProjectId = "p1", Stage = Stages.Discovery,
            StartedAt = At(1), Outcome = RunOutcome.Done, EndedAt = At(2),
        });
        await _store.UpsertChatMessageAsync(new ChatMessageDoc
        {
            Id = RecordIds.ChatMessage("p1", Stages.Discovery, "aaaa1111"), ProjectId = "p1",
            Stage = Stages.Discovery, Text = "earlier", Status = ChatStatus.Answered, CreatedAt = At(3),
        });

        var res = await PostMessage("p1", Stages.Discovery, "and now?");

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("seq").GetInt32());
    }

    /// `queued: true` ⇒ a run is in flight, which is what lets the UI say the agent will get to this when the
    /// stage finishes rather than pretending an answer is imminent.
    [Fact]
    public async Task Message_reports_queued_while_a_pipeline_is_live()
    {
        await SeedProjectAsync();
        var reached = BlockIntakeOnItsToken();
        Assert.True(_supervisor.TryStart("p1"));
        await reached.Task;

        var res = await PostMessage("p1", Stages.Intake, "stop and explain");

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("queued").GetBoolean());

        Assert.True(_supervisor.CancelRun(RunIds.Run("p1", Stages.Intake, 1)));
        await _supervisor.Completion("p1");
    }

    [Fact]
    public async Task Message_refuses_blank_text()
    {
        await SeedProjectAsync();

        // Checked before any store lookup, as the chat door does: an empty turn is always a 422, never a 404.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostMessage("p1", Stages.Discovery, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostMessage("p1", Stages.Discovery, "")).StatusCode);
        Assert.Empty(_store.Documents.OfType<ChatMessageDoc>());
    }

    [Fact]
    public async Task Message_refuses_an_unknown_stage()
    {
        await SeedProjectAsync();

        var res = await PostMessage("p1", "screening", "hello?");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("unknown stage", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Message_404s_an_unknown_project()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await PostMessage("ghost", Stages.Discovery, "hello?")).StatusCode);
    }

    /// The Intake/Pool composer posts here. Pool is in Stages.All precisely so this door is open.
    [Fact]
    public async Task Message_is_accepted_on_the_pool_stage()
    {
        await SeedProjectAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await PostMessage("p1", Stages.Pool, "why no Hf?")).StatusCode);
    }

    // ---- GET …/runs ---------------------------------------------------------------------------------

    /// §7.3's replay/audit read. It crosses the whole project, and it goes out through the SAME projection
    /// the thread does — a raw RunDoc would put `runId === undefined` in front of a client coding against §7.
    [Fact]
    public async Task Runs_lists_every_run_oldest_first_and_filters_by_stage()
    {
        await SeedProjectAsync();
        await _runs.UpsertAsync(new RunDoc
        {
            Id = RunIds.Run("p1", Stages.Intake, 1), ProjectId = "p1", Stage = Stages.Intake,
            Agent = "intake", StartedAt = At(1), Outcome = RunOutcome.Done, EndedAt = At(2),
        });
        await _runs.UpsertAsync(new RunDoc
        {
            Id = RunIds.Run("p1", Stages.Discovery, 1), ProjectId = "p1", Stage = Stages.Discovery,
            StartedAt = At(3),
        });

        var all = (await _client.GetFromJsonAsync<JsonElement[]>("/projects/p1/runs"))!;
        Assert.Equal(
            [RunIds.Run("p1", Stages.Intake, 1), RunIds.Run("p1", Stages.Discovery, 1)],
            all.Select(r => r.GetProperty("runId").GetString()));
        Assert.False(all[0].TryGetProperty("projectId", out _));
        // `agent: null` is how the client tells a deterministic stage from an agent run — present, not absent.
        Assert.Equal(JsonValueKind.Null, all[1].GetProperty("agent").ValueKind);

        var discovery = await _client.GetFromJsonAsync<JsonElement[]>("/projects/p1/runs?stage=discovery");
        Assert.Equal(Stages.Discovery, Assert.Single(discovery!).GetProperty("stage").GetString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await _client.GetAsync("/projects/p1/runs?stage=screening")).StatusCode);
    }

    // ---- POST …/runs/{runId}/cancel -----------------------------------------------------------------

    private async Task<RunDoc> SeedRunAsync(string id, string outcome, string? parentRunId = null)
    {
        var run = new RunDoc
        {
            Id = id, ProjectId = "p1", Stage = Stages.Regulatory, StartedAt = At(1), Outcome = outcome,
            ParentRunId = parentRunId, EndedAt = outcome == RunOutcome.Running ? null : At(9),
        };
        await _runs.UpsertAsync(run);
        return run;
    }

    private Task<HttpResponseMessage> PostCancel(string runId) =>
        _client.PostAsync($"/projects/p1/runs/{Uri.EscapeDataString(runId)}/cancel", null);

    /// THE assertion of this endpoint. Cancelling one substance of fourteen leaves a candidate set that LOOKS
    /// screened and is not — the parent is the only granularity at which cancel is honest.
    [Fact]
    public async Task Cancel_refuses_a_regulatory_child_run()
    {
        await SeedProjectAsync();
        var parent = RunIds.Run("p1", Stages.Regulatory, 1);
        await SeedRunAsync($"{parent}|1314-23-4|bottle", RunOutcome.Running, parentRunId: parent);

        var res = await PostCancel($"{parent}|1314-23-4|bottle");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("parent", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Cancel_409s_a_run_that_already_ended()
    {
        await SeedProjectAsync();
        await SeedRunAsync(RunIds.Run("p1", Stages.Regulatory, 1), RunOutcome.Done);

        Assert.Equal(HttpStatusCode.Conflict, (await PostCancel(RunIds.Run("p1", Stages.Regulatory, 1))).StatusCode);
    }

    /// A run the store calls `running` that no process is holding — the trail of a dead process. There is
    /// nothing to cancel, and saying so is more honest than a 202 that does nothing.
    [Fact]
    public async Task Cancel_409s_a_run_no_process_is_holding()
    {
        await SeedProjectAsync();
        await SeedRunAsync(RunIds.Run("p1", Stages.Regulatory, 1), RunOutcome.Running);

        Assert.Equal(HttpStatusCode.Conflict, (await PostCancel(RunIds.Run("p1", Stages.Regulatory, 1))).StatusCode);
    }

    /// A run id is '|'-separated ("run|p1|discovery|1"), and it travels in a PATH SEGMENT. The client is not
    /// obliged to percent-encode it, so both forms have to route — an id that only worked encoded would be a
    /// 404 the operator sees as "that run doesn't exist".
    [Fact]
    public async Task Cancel_routes_a_run_id_whether_or_not_its_pipes_are_encoded()
    {
        await SeedProjectAsync();
        await SeedRunAsync(RunIds.Run("p1", Stages.Regulatory, 1), RunOutcome.Done);
        var raw = RunIds.Run("p1", Stages.Regulatory, 1);

        // 409 (not 404) is the proof: the endpoint FOUND the run and refused it for its outcome.
        Assert.Equal(HttpStatusCode.Conflict,
            (await _client.PostAsync($"/projects/p1/runs/{raw}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostCancel(raw)).StatusCode);
    }

    [Fact]
    public async Task Cancel_404s_an_unknown_run()
    {
        await SeedProjectAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await PostCancel(RunIds.Run("p1", Stages.Regulatory, 7))).StatusCode);
    }

    [Fact]
    public async Task Cancel_stops_a_live_run_and_records_it_as_the_operators_decision()
    {
        await SeedProjectAsync();
        var reached = BlockIntakeOnItsToken();
        Assert.True(_supervisor.TryStart("p1"));
        await reached.Task;

        var res = await PostCancel(RunIds.Run("p1", Stages.Intake, 1));

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await _supervisor.Completion("p1");
        var run = await _runs.GetAsync("p1", RunIds.Run("p1", Stages.Intake, 1));
        Assert.Equal(RunOutcome.Cancelled, run!.Outcome);
        // The STAGE lands in needs-review, not `cancelled`: an operator-cancelled stage is one a human has to
        // look at, and that is the status the dashboard already owns.
        Assert.Equal("needs-review", (await _store.GetProjectAsync("p1"))!.Stages[Stages.Intake].Status);
    }

    // ---- POST …/stages/{stage}/rerun ----------------------------------------------------------------

    private Task<HttpResponseMessage> PostRerun(string pid, string stage) =>
        _client.PostAsync($"/projects/{pid}/stages/{stage}/rerun", null);

    /// THE assertion of this endpoint. Re-running a landed stage replaces analysis a gate may have been
    /// signed over; revise-with-reason is the path that does that WITH the operator's reason recorded, and
    /// rerun must not become the backdoor around it.
    [Fact]
    public async Task Rerun_refuses_a_done_stage()
    {
        var project = await SeedProjectAsync();
        project.Stages[Stages.Discovery].Status = "done";
        await _store.UpsertProjectAsync(project);

        var res = await PostRerun("p1", Stages.Discovery);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("tell the agent why", body.GetProperty("error").GetString());
        Assert.Equal("done", (await _store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Status);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("pending")]
    [InlineData(StageStatus.Done)]   // a finished stage is not "finished badly" either
    public async Task Rerun_refuses_a_stage_that_is_not_finished_badly(string status)
    {
        var project = await SeedProjectAsync();
        project.Stages[Stages.Regulatory].Status = status;
        await _store.UpsertProjectAsync(project);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostRerun("p1", Stages.Regulatory)).StatusCode);
    }

    [Fact]
    public async Task Rerun_of_a_failed_stage_resets_it_and_actually_runs_it_again()
    {
        var project = await SeedProjectAsync();
        project.Stages[Stages.Intake].Status = "done";
        project.Stages[Stages.Discovery].Status = "failed";
        project.Stages[Stages.Discovery].Error = "the model returned unparseable candidates";
        await _store.UpsertProjectAsync(project);
        // Intake's output on file, and an element pool so the pool stage has nothing to propose.
        await _store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints("p1"), ProjectId = "p1",
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ElementPools = [new("bottle", "Zr", "Kα", "V", null)],
        });

        var res = await PostRerun("p1", Stages.Discovery);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await _supervisor.Completion("p1");
        // A stage the dispatcher would never have re-entered on a second pass: `failed` is not `pending`, so
        // the reset is what makes rerun mean anything at all.
        Assert.NotNull(await _store.GetCandidatesAsync("p1"));
        var discovery = (await _store.GetProjectAsync("p1"))!.Stages[Stages.Discovery];
        Assert.Equal("done", discovery.Status);
        Assert.Null(discovery.Error);
    }

    [Fact]
    public async Task Rerun_accepts_a_needs_review_stage()
    {
        var project = await SeedProjectAsync();
        project.Stages[Stages.Discovery].Status = "needs-review";
        await _store.UpsertProjectAsync(project);

        Assert.Equal(HttpStatusCode.Accepted, (await PostRerun("p1", Stages.Discovery)).StatusCode);
        await _supervisor.Completion("p1");
    }

    /// `cancelled` is in §7.3's allowed set even though the runner currently stamps an operator-cancelled
    /// stage `needs-review` — the contract is the client's, not the runner's, and it must not 422 on a status
    /// the spec says is re-runnable.
    [Fact]
    public async Task Rerun_accepts_a_cancelled_stage()
    {
        var project = await SeedProjectAsync();
        project.Stages[Stages.Discovery].Status = "cancelled";
        await _store.UpsertProjectAsync(project);

        Assert.Equal(HttpStatusCode.Accepted, (await PostRerun("p1", Stages.Discovery)).StatusCode);
        await _supervisor.Completion("p1");
    }

    [Fact]
    public async Task Rerun_refuses_an_unknown_stage_and_404s_an_unknown_project()
    {
        await SeedProjectAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostRerun("p1", "screening")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostRerun("ghost", Stages.Discovery)).StatusCode);
    }

    /// One pipeline per project. A rerun while one is live is a 409 — not a second walk over the same stages.
    [Fact]
    public async Task Rerun_409s_while_a_pipeline_is_already_live()
    {
        var project = await SeedProjectAsync();
        project.Stages[Stages.Discovery].Status = "failed";
        await _store.UpsertProjectAsync(project);
        var reached = BlockIntakeOnItsToken();
        Assert.True(_supervisor.TryStart("p1"));
        await reached.Task;

        var res = await PostRerun("p1", Stages.Discovery);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.True(_supervisor.CancelRun(RunIds.Run("p1", Stages.Intake, 1)));
        await _supervisor.Completion("p1");
    }
}
