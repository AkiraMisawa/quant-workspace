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

    const string TenYearSwap = "jpy 10y 1bn pay";

    Task<HttpResponseMessage> Command(string capability, string port, JsonObject value, string? key = null) =>
        client.PostAsJsonAsync($"/api/agent/commands/{capability}/1.0.0",
            new { inputs = new JsonObject { [port] = value }, idempotencyKey = key ?? Guid.NewGuid().ToString() });

    Task<HttpResponseMessage> Create(string workspaceId, string terms, string? key = null) =>
        Command("entity.create", "request", new JsonObject { ["product"] = RatesProducts.Irs, ["workspaceId"] = workspaceId, ["terms"] = terms }, key);

    async Task<string> NewWorkspace() =>
        (await Ok(await Command("rates.workspace.create", "request", new JsonObject { ["valuationDate"] = "2026-10-27" })))["outputs"]!["workspace"]!["workspaceId"]!.GetValue<string>();

    async Task<JsonObject> Row(string workspaceId, string line) =>
        (await Ok(await client.GetAsync($"/api/rates/workspaces/{workspaceId}")))["lines"]!.AsArray().Single(l => l!["line"]!.GetValue<string>() == line)!.AsObject();

    static async Task<JsonObject> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact] // ADR-0005 §3, §5, ADR-0008 §5: the AI makes a workspace and a line in it from short terms; the log says both were the AI's
    public async Task The_AI_creates_a_line_in_a_new_workspace_and_the_log_says_so()
    {
        var id = await NewWorkspace();
        var created = (await Ok(await Create(id, TenYearSwap)))["outputs"]!["entity"]!;
        var line = created["entity"]!.GetValue<string>();

        var row = await Row(id, line);
        Assert.True(row["priceable"]!.GetValue<bool>());
        Assert.Equal(1, row["parLeg"]!.GetValue<int>());
        Assert.Contains("fixedFrequency from profile:", created["defaulted"]!.GetValue<string>());

        var log = host.Services.GetRequiredService<WorkspaceService>().Find(id)!.Log;
        // The schedule the line derives is the workspace's own work, not anybody's edit, and states no origin.
        Assert.All(log.Where(e => e.Kind != EditKind.Derive), e => Assert.Equal(RunOrigin.Ai, e.Origin));
        Assert.Contains(log, e => e.Kind == EditKind.Create && e.Target?.Entity == line);
    }

    [Fact] // C47: an IRS given no quote is at the money: spread 0 on the floating leg, the fixed leg solved at par
    public async Task An_irs_without_quotes_is_at_the_money()
    {
        var id = await NewWorkspace();
        var row = await Row(id, (await Ok(await Create(id, TenYearSwap)))["outputs"]!["entity"]!["entity"]!.GetValue<string>());

        Assert.True(row["priceable"]!.GetValue<bool>());
        Assert.Equal(1, row["parLeg"]!.GetValue<int>());
        Assert.Equal("FloatSpreadPercent", row["fields"]!["leg2.quoteKind"]!.GetValue<string>());
        Assert.Equal(0, row["fields"]!["leg2.quote"]!.GetValue<double>());
    }

    [Fact] // C47: a fixed rate the user gives makes an off-market swap; the spread stays 0 and no leg is solved
    public async Task A_given_rate_replaces_par_and_keeps_spread_zero()
    {
        var id = await NewWorkspace();
        var row = await Row(id, (await Ok(await Create(id, $"{TenYearSwap} 1.25%")))["outputs"]!["entity"]!["entity"]!.GetValue<string>());

        Assert.True(row["priceable"]!.GetValue<bool>());
        Assert.Null(row["parLeg"]);
        Assert.Equal(1.25, row["fields"]!["leg1.quote"]!.GetValue<double>());
        Assert.Equal(0, row["fields"]!["leg2.quote"]!.GetValue<double>());
    }

    [Fact] // ADR-0005 §4, ADR-0008 §4c: every term no default supplies is refused by name, so the AI asks the user; nothing is created
    public async Task Missing_terms_are_refused_by_name_and_nothing_is_created()
    {
        var id = await NewWorkspace();

        var response = await Create(id, "10y");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var paths = (await response.Content.ReadFromJsonAsync<JsonObject>())!["issues"]!.AsArray().Select(i => i!["path"]!.GetValue<string>()).ToHashSet();
        Assert.Equal(new HashSet<string>
        {
            "/inputs/request/terms/currency", "/inputs/request/terms/index", "/inputs/request/terms/notional", "/inputs/request/terms/direction",
            "/inputs/request/terms/fixedFrequency", "/inputs/request/terms/floatFrequency",
        }, paths);
        Assert.Empty((await Ok(await client.GetAsync($"/api/rates/workspaces/{id}")))["lines"]!.AsArray());
    }

    [Fact] // ADR-0005 §4, ADR-0008 §7: terms that read but could not be priced are refused with the resolver's reasons
    public async Task Unpriceable_terms_are_refused_with_every_reason()
    {
        var id = await NewWorkspace();

        // A CCS with no basis spread reads as complete, and the Rates module's economic check still refuses it.
        var ccs = await Command("entity.create", "request", new JsonObject { ["product"] = RatesProducts.Ccs, ["workspaceId"] = id, ["terms"] = "5y usd jpy 100 15bn pay" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, ccs.StatusCode);
        var messages = (await ccs.Content.ReadFromJsonAsync<JsonObject>())!["issues"]!.AsArray().Select(i => i!["message"]!.GetValue<string>()).ToList();
        Assert.Contains(messages, m => m.Contains("at par"));
        Assert.Empty((await Ok(await client.GetAsync($"/api/rates/workspaces/{id}")))["lines"]!.AsArray());
    }

    [Fact] // ADR-0005 §2: a retry with the same key answers the first result; it does not add a second line
    public async Task A_retried_command_does_not_act_twice()
    {
        var id = await NewWorkspace();
        var first = await Ok(await Create(id, TenYearSwap, "retry-1"));
        var retry = await Ok(await Create(id, TenYearSwap, "retry-1"));
        var other = await Create(id, "jpy 5y 1bn pay", "retry-1");

        Assert.Equal(first.ToJsonString(), retry.ToJsonString());
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Single((await Ok(await client.GetAsync($"/api/rates/workspaces/{id}")))["lines"]!.AsArray());
    }

    [Fact] // ADR-0005 §5, owner 2026-10-27: the AI joins the page's subscription rather than starting a second, and its stop is recorded
    public async Task The_AI_joins_the_running_subscription_and_its_stop_is_recorded()
    {
        var id = await NewWorkspace();
        await Ok(await Create(id, TenYearSwap));
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
            [new WorkflowStep("create", "entity.create", "1.0.0", [],
                new Dictionary<string, Binding> { ["request"] = new ConstantBinding(new JsonObject { ["product"] = RatesProducts.Irs, ["workspaceId"] = "ws", ["terms"] = TenYearSwap }) }, FailurePolicy.Stop)],
            new Dictionary<string, Binding> { ["entity"] = new OutputBinding("create", "entity") });

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
