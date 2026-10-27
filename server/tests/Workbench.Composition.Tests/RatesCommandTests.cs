using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Rates;

namespace Workbench.Composition.Tests;

// ADR-0005: the Rates commands, as the agent's tool calls reach them. The caller is the server's; every missing term
// is refused by name; the page and the AI act through the same service and the log says which one did.
public class RatesCommandTests : IClassFixture<WorkbenchFactory>
{
    readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host;
    readonly HttpClient client;

    public RatesCommandTests(WorkbenchFactory factory)
    {
        host = factory.WithWebHostBuilder(b => b.UseSetting("Rates:Ticker", "false"));
        client = host.CreateClient();
    }

    static JsonObject TenYearSwap(string? workspaceId = null) => new()
    {
        [workspaceId is null ? "valuationDate" : "workspaceId"] = workspaceId ?? "2026-10-27",
        ["product"] = "irs", ["end"] = "10y", ["direction"] = "pay",
        ["leg1.currency"] = "JPY", ["leg1.index"] = "TONA", ["leg1.notional"] = 1_000_000_000, ["leg1.paymentFrequency"] = "12m",
        ["leg2.currency"] = "JPY", ["leg2.index"] = "TONA", ["leg2.notional"] = 1_000_000_000, ["leg2.paymentFrequency"] = "12m",
        ["leg2.quoteKind"] = "FloatSpreadPercent", ["leg2.quote"] = 0,
    };

    Task<HttpResponseMessage> Command(string capability, string port, JsonObject value, string? key = null) =>
        client.PostAsJsonAsync($"/api/agent/commands/{capability}/1.0.0",
            new { inputs = new JsonObject { [port] = value }, idempotencyKey = key ?? Guid.NewGuid().ToString() });

    static async Task<JsonObject> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    int Workspaces() => host.Services.GetRequiredService<RatesWorkspaces>().OwnedBy("demo").Count;

    [Fact] // ADR-0005 §3, §5: a line the AI creates is the page's line, priceable, and logged as the AI's edit
    public async Task The_AI_creates_a_line_in_a_new_workspace_and_the_log_says_so()
    {
        var done = await Ok(await Command("rates.line.create", "terms", TenYearSwap()));
        var created = done["outputs"]!["line"]!;
        var id = created["workspaceId"]!.GetValue<string>();
        var line = created["line"]!.GetValue<string>();

        var view = await Ok(await client.GetAsync($"/api/rates/workspaces/{id}"));
        var row = view["lines"]!.AsArray().Single(l => l!["line"]!.GetValue<string>() == line)!;
        Assert.True(row["priceable"]!.GetValue<bool>());
        Assert.Equal(1, row["parLeg"]!.GetValue<int>());
        Assert.Equal("2026-10-27", view["valuationDate"]!.GetValue<string>());

        var log = host.Services.GetRequiredService<WorkspaceService>().Find(id)!.Log;
        // The schedule the line derives is the workspace's own work, not anybody's edit, and states no origin.
        Assert.All(log.Where(e => e.Kind != EditKind.Derive), e => Assert.Equal(RunOrigin.Ai, e.Origin));
        Assert.Contains(log, e => e.Kind == EditKind.Create && e.Target?.Entity == line);
    }

    [Fact] // ADR-0005 §4: every missing term is refused by name, so the AI asks the user; nothing is created
    public async Task Missing_terms_are_refused_by_name_and_nothing_is_created()
    {
        var before = Workspaces();

        var response = await Command("rates.line.create", "terms", new JsonObject { ["valuationDate"] = "2026-10-27", ["product"] = "irs", ["end"] = "10y" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var paths = (await response.Content.ReadFromJsonAsync<JsonObject>())!["issues"]!.AsArray().Select(i => i!["path"]!.GetValue<string>()).ToHashSet();
        Assert.Equal(new HashSet<string>
        {
            "/inputs/terms/direction",
            "/inputs/terms/leg1.currency", "/inputs/terms/leg1.index", "/inputs/terms/leg1.notional", "/inputs/terms/leg1.paymentFrequency",
            "/inputs/terms/leg2.currency", "/inputs/terms/leg2.index", "/inputs/terms/leg2.notional", "/inputs/terms/leg2.paymentFrequency",
        }, paths);
        Assert.Equal(before, Workspaces());
    }

    [Fact] // ADR-0005 §4, C40: terms that could not be priced are refused with the resolver's reasons, never completed
    public async Task Unpriceable_terms_are_refused_with_every_reason()
    {
        var before = Workspaces();
        var terms = TenYearSwap();
        terms.Remove("leg2.quoteKind");
        terms.Remove("leg2.quote");
        terms["leg2.currency"] = "USD";

        var response = await Command("rates.line.create", "terms", terms);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var messages = (await response.Content.ReadFromJsonAsync<JsonObject>())!["issues"]!.AsArray().Select(i => i!["message"]!.GetValue<string>()).ToList();
        Assert.Contains(messages, m => m.Contains("Both legs are at par"));
        Assert.Contains(messages, m => m.Contains("An IRS has one currency"));
        Assert.Equal(before, Workspaces());
    }

    [Fact] // ADR-0005 §2: a retry with the same key answers the first result; it does not add a second line
    public async Task A_retried_command_does_not_act_twice()
    {
        var first = await Ok(await Command("rates.line.create", "terms", TenYearSwap(), "retry-1"));
        var id = first["outputs"]!["line"]!["workspaceId"]!.GetValue<string>();
        var retry = await Ok(await Command("rates.line.create", "terms", TenYearSwap(), "retry-1"));
        var other = await Command("rates.line.create", "terms", TenYearSwap(id), "retry-1");

        Assert.Equal(first.ToJsonString(), retry.ToJsonString());
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Single((await Ok(await client.GetAsync($"/api/rates/workspaces/{id}")))["lines"]!.AsArray());
    }

    [Fact] // ADR-0005 §5, owner 2026-10-27: the AI joins the page's subscription rather than starting a second, and its stop is recorded
    public async Task The_AI_joins_the_running_subscription_and_its_stop_is_recorded()
    {
        var id = (await Ok(await Command("rates.line.create", "terms", TenYearSwap())))["outputs"]!["line"]!["workspaceId"]!.GetValue<string>();
        var page = (await Ok(await client.PostAsync($"/api/rates/workspaces/{id}/live", null)))["subscriptionId"]!.GetValue<string>();

        var started = await Ok(await Command("rates.live.start", "workspace", new JsonObject { ["workspaceId"] = id }));
        Assert.Equal(page, started["outputs"]!["subscription"]!["subscriptionId"]!.GetValue<string>());

        await Ok(await Command("rates.live.stop", "workspace", new JsonObject { ["workspaceId"] = id }));
        var live = (await Ok(await client.GetAsync($"/api/rates/workspaces/{id}")))["live"]!;
        Assert.Equal((false, "user", "pricer", "ai"),
            (live["running"]!.GetValue<bool>(), live["stoppedBy"]!.GetValue<string>(), live["startedFrom"]!.GetValue<string>(), live["stoppedFrom"]!.GetValue<string>()));

        var again = await Command("rates.live.stop", "workspace", new JsonObject { ["workspaceId"] = id });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
    }

    [Fact] // ADR-0005 §2: a command is never a workflow step, so a Run only computes and persists
    public void A_command_is_refused_as_a_workflow_step()
    {
        var registry = host.Services.GetRequiredService<CompositionRegistry>();
        var workflow = new WorkflowDefinition("workflow", CompositionValidator.SchemaVersion, "workflow.test.command", "1.0.0", "Command as a step",
            RatesContracts.PriceInput, RatesContracts.Version,
            [new WorkflowStep("create", "rates.line.create", RatesContracts.Version, [],
                new Dictionary<string, Binding> { ["terms"] = new ConstantBinding(TenYearSwap()) }, FailurePolicy.Stop)],
            new Dictionary<string, Binding> { ["line"] = new OutputBinding("create", "line") });

        Assert.Contains(new CompositionValidator(registry).Validate(workflow), i => i.Code == "CommandAsStep");
    }

    [Fact] // ADR-0005 §2: a capability that computes in a Run is not invoked as a command
    public async Task A_computing_capability_is_not_a_command()
    {
        var response = await Command("rates.price", "acceptedInput", new JsonObject());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("NotACommand", (await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
    }
}
