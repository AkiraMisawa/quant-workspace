using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Agent;
using Workbench.AgentHost;
using Workbench.AgentHost.Contracts;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Samples.Rates;
using Workbench.Web;

namespace Workbench.Composition.Tests;

// ADR-0007 §5, C44: the Copilot runtime. The workbench posts each turn to the agent host, a process of its own that
// holds the model credential; the host answers the model's words and tool calls, and every call is invoked in the
// workbench for the signed-in user. The model here is a fake behind the host's backend port, so the wire, the host and
// the workbench's side are the real ones; the Copilot SDK itself is exercised on the owner's machine.
public class CopilotRuntimeTests(WorkbenchFactory factory) : IClassFixture<WorkbenchFactory>, IAsyncLifetime
{
    const string Key = "test-key-0123456789-0123456789-0123456789";
    readonly FakeModel model = new();
    WebApplication? agentHost;
    WebApplicationFactory<Program>? workbench;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["AgentHost:Key"] = Key;
        builder.Services.AddSingleton<IModelBackend>(model);
        agentHost = AgentHostApp.Build(builder);
        await agentHost.StartAsync();
        var handler = agentHost.GetTestServer().CreateHandler();
        workbench = factory.WithWebHostBuilder(b => b
            .UseSetting("Rates:Ticker", "false")
            .UseSetting("Agent:Runtime", "copilot")
            .UseSetting("Agent:Copilot:HostUrl", "http://agent-host/")
            .UseSetting("Agent:Copilot:HostKey", Key)
            .UseSetting("Agent:Copilot:Model", "fake-model")
            .ConfigureTestServices(s => s.AddHttpClient(Program.CopilotHttpClient).ConfigurePrimaryHttpMessageHandler(() => handler)));
    }

    public async Task DisposeAsync()
    {
        workbench?.Dispose();
        if (agentHost is not null) await agentHost.DisposeAsync();
    }

    WebApplicationFactory<Program> Workbench => workbench!;

    static readonly JsonObject SwapTerms = new()
    {
        ["terms"] = new JsonObject
        {
            ["product"] = "irs", ["valuationDate"] = "2026-10-27", ["end"] = "10y", ["direction"] = "pay",
            ["leg1.paymentFrequency"] = "12m", ["leg2.paymentFrequency"] = "12m",
            ["leg2.quoteKind"] = "FloatSpreadPercent", ["leg2.quote"] = 0,
            ["leg1.currency"] = "JPY", ["leg1.index"] = "TONA", ["leg1.notional"] = 1_000_000_000,
            ["leg2.currency"] = "JPY", ["leg2.index"] = "TONA", ["leg2.notional"] = 1_000_000_000,
        },
    };

    static JsonObject Input(string threadId, string runId, string messageId, string text, params (string Description, string Value)[] context) => new()
    {
        ["threadId"] = threadId,
        ["runId"] = runId,
        ["messages"] = new JsonArray(new JsonObject { ["id"] = messageId, ["role"] = "user", ["content"] = text }),
        ["tools"] = new JsonArray(),
        ["context"] = new JsonArray([.. context.Select(c => (JsonNode)new JsonObject { ["description"] = c.Description, ["value"] = c.Value })]),
    };

    async Task<List<JsonObject>> Run(JsonObject input, HttpClient? client = null)
    {
        var response = await (client ?? Workbench.CreateClient()).PostAsJsonAsync("/api/agent/runs", input);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadAsStringAsync();
        return [.. body.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => (JsonObject)JsonNode.Parse(l[6..])!)];
    }

    static IEnumerable<string> Types(IEnumerable<JsonObject> events) => events.Select(e => e["type"]!.GetValue<string>());

    static string Reply(IEnumerable<JsonObject> events) =>
        string.Concat(events.Where(e => e["type"]!.GetValue<string>() == "TEXT_MESSAGE_CONTENT").Select(e => e["delta"]!.GetValue<string>()));

    int Workspaces() => Workbench.Services.GetRequiredService<RatesWorkspaces>().OwnedBy(Workbench.Services.GetRequiredService<CurrentUser>().Id).Count;

    [Fact] // ADR-0007 §3, §5, C44: the model's tool call runs as the user's command in the workbench, and the run says it is a model
    public async Task A_Copilot_tool_call_runs_as_the_users_command_in_the_workbench()
    {
        string? seen = null;
        model.Answer = async (turn, sink, ct) =>
        {
            seen = await sink.CallAsync("call-1", "rates_line_create", SwapTerms.ToJsonString(), ct);
            sink.Say("Made the swap.");
        };
        var before = Workspaces();

        var events = await Run(Input("c-1", "r-1", "m-1", "make a 10y JPY TONA payer, 1bn", ("Active panel", "Pricing DB")));

        Assert.Equal("RUN_FINISHED", Types(events).Last());
        var workbenchMeta = events[0]["metadata"]!["workbench"]!;
        Assert.Equal("copilot", workbenchMeta["runtime"]!.GetValue<string>());
        Assert.Equal("Copilot · fake-model", workbenchMeta["label"]!.GetValue<string>());
        Assert.False(workbenchMeta["simulated"]!.GetValue<bool>());
        Assert.Equal(["rates_line_create"], events.Where(e => e["type"]!.GetValue<string>() == "TOOL_CALL_START").Select(e => e["toolCallName"]!.GetValue<string>()));
        Assert.Equal("Made the swap.", Reply(events));

        var result = (JsonObject)JsonNode.Parse(seen!)!;
        Assert.Equal("done", result["status"]!.GetValue<string>());
        var id = result["outputs"]!["line"]!["workspaceId"]!.GetValue<string>();
        Assert.Equal(before + 1, Workspaces());
        var log = Workbench.Services.GetRequiredService<WorkspaceService>().Find(id)!.Log;
        Assert.All(log.Where(e => e.Kind != EditKind.Derive), e => Assert.Equal(RunOrigin.Ai, e.Origin));

        // The host was offered the workbench's commands and nothing else, with the request's context and no history yet.
        var turn = Assert.Single(model.Turns);
        Assert.Equal("fake-model", turn.Model);
        Assert.Contains("rates_line_create", turn.Tools.Select(t => t.Name));
        Assert.All(turn.Tools, t => Assert.NotNull(Workbench.Services.GetRequiredService<AgentToolGateway>().CapabilityOf(t.Name)));
        Assert.Contains("- Active panel: Pricing DB", turn.Prompt, StringComparison.Ordinal);
        Assert.Null(turn.Earlier);
    }

    [Fact] // ADR-0007 §2: the conversation is the workbench's, and goes with each turn for a host that has lost its session
    public async Task Each_turn_carries_the_conversation_before_it()
    {
        model.Answer = (turn, sink, ct) =>
        {
            sink.Say(turn.Earlier is null ? "Which currency?" : "JPY it is.");
            return Task.CompletedTask;
        };
        var client = Workbench.CreateClient();
        await Run(Input("c-2", "r-1", "m-1", "make a swap"), client);
        var events = await Run(Input("c-2", "r-2", "m-2", "JPY"), client);

        Assert.Equal("JPY it is.", Reply(events));
        Assert.Equal("User: make a swap\nAssistant: Which currency?\n", model.Turns[1].Earlier);
        Assert.Equal("JPY", model.Turns[1].Prompt);
    }

    [Fact] // ADR-0007 §3 (AI06): a tool the model invents is refused by the workbench, and the refusal goes back to the model
    public async Task A_tool_the_model_invents_is_refused_and_the_model_is_told()
    {
        string? seen = null;
        model.Answer = async (turn, sink, ct) => seen = await sink.CallAsync("call-1", "run_sql", "{\"sql\":\"delete from Workspace\"}", ct);

        var events = await Run(Input("c-3", "r-1", "m-1", "clean up"));

        Assert.Equal("RUN_FINISHED", Types(events).Last());
        var result = (JsonObject)JsonNode.Parse(seen!)!;
        Assert.Equal("refused", result["status"]!.GetValue<string>());
        Assert.Equal("UnknownTool", result["code"]!.GetValue<string>());
    }

    [Fact] // ADR-0007 §4: stopping a run stops the model's turn too
    public async Task Cancelling_a_run_aborts_the_models_turn()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Answer = async (turn, sink, ct) =>
        {
            asked.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                aborted.TrySetResult();
            }
        };
        var client = Workbench.CreateClient();
        var stream = Run(Input("c-4", "r-1", "m-1", "think hard"), client);
        await asked.Task;

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/agent/threads/c-4/runs/r-1/cancel", null)).StatusCode);
        var events = await stream;

        Assert.Equal("cancelled", events[^1]["outcome"]!["type"]!.GetValue<string>());
        await aborted.Task;
    }

    [Fact] // ADR-0007 §5: a failure at the model ends the run with what the model's side said, and nothing stands in for it
    public async Task A_model_failure_ends_the_run_with_its_message()
    {
        model.Answer = (_, _, _) => throw new ModelFailed("Copilot quota exhausted");

        var events = await Run(Input("c-5", "r-1", "m-1", "anything"));

        var error = events[^1];
        Assert.Equal("RUN_ERROR", error["type"]!.GetValue<string>());
        Assert.Equal("AgentFailed", error["code"]!.GetValue<string>());
        Assert.Contains("Copilot quota exhausted", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact] // C44: the host answers only a caller holding the shared key, and does not start without one
    public async Task The_host_refuses_a_caller_without_the_key_and_will_not_start_without_one()
    {
        var anonymous = agentHost!.GetTestServer().CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/models")).StatusCode);
        anonymous.DefaultRequestHeaders.Add(AgentHostWire.KeyHeader, "wrong-key-0123456789-0123456789-0123456789");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/models")).StatusCode);

        var keyless = WebApplication.CreateBuilder();
        keyless.WebHost.UseTestServer();
        keyless.Services.AddSingleton<IModelBackend>(model);
        var failure = Assert.Throws<InvalidOperationException>(() => AgentHostApp.Build(keyless));
        Assert.Contains("AgentHost:Key", failure.Message, StringComparison.Ordinal);
    }

    [Fact] // ADR-0007 §5: the Copilot runtime without its settings stops startup instead of falling back to the scripted one
    public void Copilot_without_its_settings_stops_startup()
    {
        var unset = factory.WithWebHostBuilder(b => b.UseSetting("Agent:Runtime", "copilot"));
        var failure = Assert.Throws<InvalidOperationException>(() => unset.CreateClient());
        Assert.Contains("Agent:Copilot:", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The model behind the host, answering as each test says, and keeping the turns it was asked.</summary>
    sealed class FakeModel : IModelBackend
    {
        public List<HostTurn> Turns { get; } = [];

        public Func<HostTurn, IModelTurnSink, CancellationToken, Task> Answer { get; set; } = (_, _, _) => Task.CompletedTask;

        public Task RunAsync(HostTurn turn, IModelTurnSink sink, CancellationToken cancellationToken)
        {
            lock (Turns) Turns.Add(turn);
            return Answer(turn, sink, cancellationToken);
        }

        public Task<IReadOnlyList<string>> ModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["fake-model"]);
    }
}
